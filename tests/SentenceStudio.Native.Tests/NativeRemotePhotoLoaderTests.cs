using System.Net;
using System.Net.Http.Headers;
using SentenceStudio.Native.Adapters;

namespace SentenceStudio.Native.Tests;

public sealed class NativeRemotePhotoLoaderTests
{
    private static readonly byte[] Jpeg = [0xff, 0xd8, 0xff, 0xe0, 1, 2, 3, 4];
    private static readonly Uri ApiRoot = new("http://localhost:5081/");

    [Fact]
    public async Task FollowsHttpsRedirectToTheImage()
    {
        var server = new PhotoServer()
            .Redirect("https://picsum.photos/seed/cat/400", "https://fastly.picsum.photos/id/1/400.jpg")
            .Image("https://fastly.picsum.photos/id/1/400.jpg");
        using var loader = server.Loader();

        var bytes = await loader.LoadAsync(
            new Uri("https://picsum.photos/seed/cat/400"), CancellationToken.None);

        Assert.Equal(Jpeg, bytes);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task FollowsRelativeRedirectLocation()
    {
        var server = new PhotoServer()
            .Redirect("https://images.example/a", "/b.jpg")
            .Image("https://images.example/b.jpg");
        using var loader = server.Loader();

        Assert.Equal(Jpeg, await loader.LoadAsync(
            new Uri("https://images.example/a"), CancellationToken.None));
    }

    [Theory]
    [InlineData("http://images.example/plain.jpg")]
    [InlineData("http://localhost:5081/api/media/photo.jpg")]
    [InlineData("https://user:secret@images.example/creds.jpg")]
    [InlineData("file:///etc/passwd")]
    public async Task RefusesARedirectThatLeavesHttps(string target)
    {
        var server = new PhotoServer()
            .Redirect("https://images.example/start", target)
            .Image(target);
        using var loader = server.Loader();

        Assert.Null(await loader.LoadAsync(
            new Uri("https://images.example/start"), CancellationToken.None));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task StopsAfterTheMaximumRedirects()
    {
        var server = new PhotoServer();
        for (var hop = 0; hop <= NativeRemotePhotoLoader.MaxRedirects + 1; hop++)
            server.Redirect($"https://images.example/{hop}", $"https://images.example/{hop + 1}");
        server.Image($"https://images.example/{NativeRemotePhotoLoader.MaxRedirects + 2}");
        using var loader = server.Loader();

        Assert.Null(await loader.LoadAsync(
            new Uri("https://images.example/0"), CancellationToken.None));
        Assert.Equal(NativeRemotePhotoLoader.MaxRedirects + 1, server.Requests.Count);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    [InlineData(null)]
    public async Task RefusesAFinalResponseThatIsNotAnImage(string? mediaType)
    {
        var server = new PhotoServer()
            .Redirect("https://images.example/a", "https://images.example/b")
            .Respond("https://images.example/b", HttpStatusCode.OK, Jpeg, mediaType);
        using var loader = server.Loader();

        Assert.Null(await loader.LoadAsync(
            new Uri("https://images.example/a"), CancellationToken.None));
    }

    [Fact]
    public async Task RefusesAnImageOverTheSizeLimit()
    {
        var server = new PhotoServer()
            .Redirect("https://images.example/a", "https://images.example/b")
            .Respond("https://images.example/b", HttpStatusCode.OK,
                new byte[64], "image/jpeg", declareLength: false);
        using var loader = server.Loader(maxBytes: 32);

        Assert.Null(await loader.LoadAsync(
            new Uri("https://images.example/a"), CancellationToken.None));
    }

    [Fact]
    public async Task SendsNoAuthorizationOrCookieOnAnyHop()
    {
        var server = new PhotoServer()
            .Redirect("https://picsum.photos/seed/a/400", "https://fastly.picsum.photos/a.jpg")
            .Image("https://fastly.picsum.photos/a.jpg");
        using var loader = server.Loader();

        Assert.NotNull(await loader.LoadAsync(
            new Uri("https://picsum.photos/seed/a/400"), CancellationToken.None));
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains("Cookie"));
        });
    }

    [Fact]
    public void ProductionHandlerDoesNotAutoRedirectOrSendCredentials()
    {
        using var handler = NativeRemotePhotoLoader.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseDefaultCredentials);
        Assert.False(handler.PreAuthenticate);
        Assert.Null(handler.Credentials);
    }

    [Fact]
    public void FirstRequestAllowsHttpsOrHttpOnlyUnderTheApiRoot()
    {
        Assert.True(NativeRemotePhotoLoader.CanRequest(new("https://picsum.photos/a"), ApiRoot));
        Assert.True(NativeRemotePhotoLoader.CanRequest(new("http://localhost:5081/m/a.jpg"), ApiRoot));
        Assert.False(NativeRemotePhotoLoader.CanRequest(new("http://picsum.photos/a"), ApiRoot));
        Assert.False(NativeRemotePhotoLoader.CanRequest(new("http://localhost:5081/m/a.jpg"), null));
        Assert.False(NativeRemotePhotoLoader.CanRequest(new("https://u:p@picsum.photos/a"), ApiRoot));
    }

    private sealed class PhotoServer : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new();

        public List<HttpRequestMessage> Requests { get; } = [];

        public PhotoServer Redirect(string from, string location)
        {
            _routes[from] = () =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
                return response;
            };
            return this;
        }

        public PhotoServer Image(string at) =>
            Respond(at, HttpStatusCode.OK, Jpeg, "image/jpeg");

        public PhotoServer Respond(string at, HttpStatusCode status, byte[] body,
            string? mediaType, bool declareLength = true)
        {
            _routes[at] = () =>
            {
                HttpContent content = declareLength
                    ? new ByteArrayContent(body)
                    : new StreamContent(new MemoryStream(body));
                content.Headers.ContentType = mediaType is null
                    ? null : new MediaTypeHeaderValue(mediaType);
                return new HttpResponseMessage(status) { Content = content };
            };
            return this;
        }

        public NativeRemotePhotoLoader Loader(int maxBytes = 1024) =>
            new(maxBytes, ApiRoot, this, TimeSpan.FromSeconds(5));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_routes.TryGetValue(
                    request.RequestUri!.AbsoluteUri, out var route)
                ? route()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
