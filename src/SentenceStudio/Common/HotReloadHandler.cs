using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using CommunityToolkit.Maui.Markup;
using SentenceStudio;

namespace Common;

class HotReloadHandler : ICommunityToolkitHotReloadHandler
{
	public async void OnHotReload(IReadOnlyList<Type> types)
	{
		if (Application.Current?.Windows is null)
		{
			Trace.WriteLine($"{nameof(HotReloadHandler)} Failed: {nameof(Application)}.{nameof(Application.Current)}.{nameof(Application.Current.Windows)} is null");
			return;
		}

		foreach (var window in Application.Current.Windows)
		{
			if (window.Page is not Page currentPage)
			{
				return;
			}

			foreach (var type in types)
			{
				if (type.IsSubclassOf(typeof(Page)))
				{
					if (window.Page is AppShell shell)
					{
						if (shell.CurrentPage is Page visiblePage
							&& visiblePage.GetType() == type)
						{
							if (visiblePage.GetType().GetMethod("Build") is MethodInfo buildMethod)
							{
								await visiblePage.Dispatcher.DispatchAsync(async () =>
								{
									buildMethod.Invoke(visiblePage, null);
								});
							}
							// var currentPageShellRoute = AppShell.GetRoute(type);

							// await currentPage.Dispatcher.DispatchAsync(async () =>
							// {
							// 	await shell.GoToAsync(currentPageShellRoute, false);
							// 	shell.Navigation.RemovePage(visiblePage);
							// });

							break;
						}
					}
					else
					{
						if (currentPage.GetType().GetMethod("Build") is MethodInfo buildMethod)
						{
							await currentPage.Dispatcher.DispatchAsync(async () =>
							{
								buildMethod.Invoke(currentPage, null);
							});
						}
					}
				}
			}
		}
	}
}