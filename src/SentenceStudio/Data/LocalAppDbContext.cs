using System;
using CommunityToolkit.Datasync.Client.Http;
using CommunityToolkit.Datasync.Client.Offline;
using Microsoft.EntityFrameworkCore;

namespace SentenceStudio.Data;

public class LocalAppDbContext : OfflineDbContext
{
    public DbSet<VocabularyList> VocabularyLists => Set<VocabularyList>();
    public DbSet<VocabularyWord> VocabularyWords => Set<VocabularyWord>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        string dbPath = Constants.DatabasePath; //System.IO.Path.Combine(FileSystem.AppDataDirectory, "local.db");
        Debug.WriteLine($"Database path: {dbPath}");
        optionsBuilder.UseSqlite($"Filename={dbPath}");
        base.OnConfiguring(optionsBuilder);
    }

    protected override void OnDatasyncInitialization(DatasyncOfflineOptionsBuilder optionsBuilder)
    {
        HttpClientOptions options = new HttpClientOptions
        {
            Endpoint = new Uri(Constants.RestUrl),
            HttpPipeline = [new LoggingHandler()]
        };

#if DEBUG
        HttpClientHandler insecureHandler = GetInsecureHandler();        
        _ = optionsBuilder.UseHttpClient(new HttpClient(insecureHandler));
#endif
        _ = optionsBuilder.UseHttpClientOptions(options);
    }

    private HttpClientHandler GetInsecureHandler()
        {
            HttpClientHandler handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
            {
                if (cert != null && cert.Issuer.Equals("CN=localhost"))
                    return true;
                return errors == System.Net.Security.SslPolicyErrors.None;
            };
            return handler;
        }

    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        PushResult pushResult = await this.PushAsync(cancellationToken);
        if(!pushResult.IsSuccessful)
        {
            throw new ApplicationException($"Push failed: {pushResult.FailedRequests.FirstOrDefault().Value.ReasonPhrase}");
        }

        PullResult pullResult = await this.PullAsync(cancellationToken);
        if(!pullResult.IsSuccessful)
        {
            throw new ApplicationException($"Pull failed: {pullResult.FailedRequests.FirstOrDefault().Value.ReasonPhrase}");
        }
    }
}
