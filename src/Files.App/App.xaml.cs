// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Helpers.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Win32;
using WinRT;

namespace Files.App
{
	/// <summary>
	/// Represents the entry point of UI for Files app.
	/// </summary>
	public partial class App : Application
	{
		public static TaskCompletionSource? SplashScreenLoadingTCS { get; private set; }
		public static string? OutputPath { get; set; }

		private static FlyoutBase? _LastOpenedFlyout;
		private static bool _isWindowTeardownCompleted;
		private static bool _isWindowClosing;
		private static readonly Task<Server.AppService> serverSession = Task.Run(() =>
		{
			var server = new Server.AppService(Environment.ProcessId);
			CanRestorePreviousSession = server.CanRestorePreviousSession;
			server.Changed += (_, name) => UiDispatcher?.TryEnqueue(() =>
			{
				if (ServerChanged is not { } handlers)
					return;
				foreach (EventHandler<string> handler in handlers.GetInvocationList())
					SafetyExtensions.IgnoreExceptions(() => handler(null, name), Logger);
			});
			return server;
		});

		internal static event EventHandler<string>? ServerChanged;
		internal static bool CanRestorePreviousSession { get; private set; }
		public static FlyoutBase? LastOpenedFlyout
		{
			set
			{
				_LastOpenedFlyout = value;

				if (_LastOpenedFlyout is not null)
					_LastOpenedFlyout.Closed += LastOpenedFlyout_Closed;
			}
		}

		// TODO: Replace with DI
		public static QuickAccessManager QuickAccessManager { get; private set; } = null!;
		public static StorageHistoryWrapper HistoryWrapper { get; private set; } = null!;
		public static FileTagsManager FileTagsManager { get; private set; } = null!;
		public static LibraryManager LibraryManager { get; private set; } = null!;
		public static AppModel AppModel { get; private set; } = null!;
		public static ILogger Logger { get; private set; } = NullLogger.Instance;

		public static Microsoft.UI.Dispatching.DispatcherQueue? UiDispatcher { get; private set; }

		/// <summary>
		/// Initializes an instance of <see cref="App"/>.
		/// </summary>
		public App()
		{
			InitializeComponent();

			// Configure exception handlers
			AppLifecycleHelper.RecordFirstChanceExceptions();
			UnhandledException += (sender, e) => AppLifecycleHelper.HandleAppUnhandledException(e.Exception, true, "Application.UnhandledException", e.Message);
			AppDomain.CurrentDomain.UnhandledException += (sender, e) => AppLifecycleHelper.HandleAppUnhandledException(e.ExceptionObject as Exception, false, "AppDomain.UnhandledException");
			TaskScheduler.UnobservedTaskException += (sender, e) => AppLifecycleHelper.HandleAppUnhandledException(e.Exception, false, "TaskScheduler.UnobservedTaskException");
			AppDomain.CurrentDomain.ProcessExit += static (_, _) =>
				SafetyExtensions.IgnoreExceptions(() => Ioc.Default.GetService<FileLoggerProvider>()?.TryCompleteAndFlush(TimeSpan.FromSeconds(2)));
		}

		/// <summary>
		/// Gets invoked when the application is launched normally by the end user.
		/// </summary>
		protected override void OnLaunched(LaunchActivatedEventArgs e)
		{
			UiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
			try { System.IO.File.AppendAllText(@"C:\Users\User\AppData\Local\Temp\files_bc.txt", DateTime.Now.ToString("o") + " OnLaunched" + Environment.NewLine); } catch { } // BC_ONLAUNCHED

			// Constructed on the UI thread: the ctor subscribes the UI-thread-only Clipboard.ContentChanged
			AppModel = new AppModel();

			_ = ActivateAsync();

			async Task ActivateAsync()
			{
				var startup = Stopwatch.StartNew();
				var appActivationArguments = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs();
				var isStartupTask = appActivationArguments.Data is Windows.ApplicationModel.Activation.IStartupTaskActivatedEventArgs;

				// Build the DI container off-thread while the window initializes
				var appModel = AppModel;
				var servicesTask = Task.Run(() =>
				{
					try
					{
						var provider = AppLifecycleHelper.ConfigureHost(appModel);

						// Configure Ioc here so Ioc.Default-dependent constructions warm off-thread too
						Ioc.Default.ConfigureServices(provider);
						// Deferred: requires out-of-proc Files.App.Server (was causing DirectoryNotFoundException before server payload was present)`r`n`t`t`t`t`t`t// _ = provider.GetRequiredService<IFileTagsSettingsService>();

						// Load the appearance settings needed for the first frame off the UI thread.
						if (isStartupTask && provider.GetRequiredService<IGeneralSettingsService>().CloseToBackground)
							return provider;
						var appearanceSettings = provider.GetRequiredService<IAppearanceSettingsService>();
						_ = appearanceSettings.AppThemeMode;
						_ = appearanceSettings.AppThemeBackdropMaterial;

						return provider;
					}
					catch (Exception ex)
					{
						try { System.IO.File.AppendAllText(@"C:\Users\User\AppData\Local\Temp\files_bc.txt", DateTime.Now.ToString("o") + " servicesTask EX: " + ex.GetType().FullName + " :: " + ex.Message + Environment.NewLine + ex.StackTrace + Environment.NewLine); } catch { }
						// A UI-thread-only service ctor failed off-thread; rebuilt on the UI thread below
						return null;
					}
				});

				// Native AOT opens the main page directly; JIT builds retain the splash screen.
				var showSplashScreen = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;

				if (!isStartupTask)
				{
					// Construct the window while appearance settings are loaded.
				try { System.IO.File.AppendAllText(@"C:\Users\User\AppData\Local\Temp\files_bc.txt", DateTime.Now.ToString("o") + " before MainWindow.Instance // BC_MW" + Environment.NewLine); } catch { }
					_ = MainWindow.Instance;
				}

				// Configure the DI (dependency injection) container
				try { System.IO.File.AppendAllText(@"C:\Users\User\AppData\Local\Temp\files_bc.txt", DateTime.Now.ToString("o") + " before await servicesTask" + Environment.NewLine); } catch { }
				var serviceProvider = await servicesTask;
				try { System.IO.File.AppendAllText(@"C:\Users\User\AppData\Local\Temp\files_bc.txt", DateTime.Now.ToString("o") + " after await servicesTask" + Environment.NewLine); } catch { }
				try { System.IO.File.AppendAllText(@"C:\Users\User\AppData\Local\Temp\files_bc.txt", DateTime.Now.ToString("o") + " serviceProvider null? " + (serviceProvider is null) + Environment.NewLine); } catch { }
				if (serviceProvider is null)
				{
					try { System.IO.File.AppendAllText(@"C:\Users\User\AppData\Local\Temp\files_bc.txt", DateTime.Now.ToString("o") + " rebuilding DI on UI thread" + Environment.NewLine); } catch { }
					serviceProvider = AppLifecycleHelper.ConfigureHost(appModel);
					try { Ioc.Default.ConfigureServices(serviceProvider); }
					catch (InvalidOperationException) { /* Ioc already configured by failed warmup */ }
				}

				try { System.IO.File.AppendAllText(@"C:\Users\User\AppData\Local\Temp\files_bc.txt", DateTime.Now.ToString("o") + " before IUserSettingsService" + Environment.NewLine); } catch { }
				var userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
				try { System.IO.File.AppendAllText(@"C:\Users\User\AppData\Local\Temp\files_bc.txt", DateTime.Now.ToString("o") + " after IUserSettingsService" + Environment.NewLine); } catch { }
				try { System.IO.File.AppendAllText(@"C:\Users\User\AppData\Local\Temp\files_bc.txt", DateTime.Now.ToString("o") + " before serverInitialization assign" + Environment.NewLine); } catch { }
				var serverInitialization = SafetyExtensions.IgnoreExceptions(async () =>
				{
					await serverSession;
					await Server.AppService.InitializeAsync();
				}, Logger);
				try { System.IO.File.AppendAllText(@"C:\Users\User\AppData\Local\Temp\files_bc.txt", DateTime.Now.ToString("o") + " before CloseToBackground check" + Environment.NewLine); } catch { }
				if (isStartupTask && userSettingsService.GeneralSettingsService.CloseToBackground)
				{
					await serverInitialization;
					await SafetyExtensions.IgnoreExceptions(async () => (await serverSession).Dispose(), Logger);
					Exit();
					return;
				}

				if (isStartupTask)
					Program.RegisterCurrentInstance();

				// TODO: Replace with DI
				try { System.IO.File.AppendAllText(@"C:\Users\User\AppData\Local\Temp\files_bc.txt", DateTime.Now.ToString("o") + " before QuickAccessManager" + Environment.NewLine); } catch { }
				QuickAccessManager = Ioc.Default.GetRequiredService<QuickAccessManager>();
				HistoryWrapper = Ioc.Default.GetRequiredService<StorageHistoryWrapper>();
				FileTagsManager = Ioc.Default.GetRequiredService<FileTagsManager>();
				LibraryManager = Ioc.Default.GetRequiredService<LibraryManager>();
				try { System.IO.File.AppendAllText(@"C:\Users\User\AppData\Local\Temp\files_bc.txt", DateTime.Now.ToString("o") + " before Logger" + Environment.NewLine); } catch { }
				Logger = Ioc.Default.GetRequiredService<ILogger<App>>();
				try { System.IO.File.AppendAllText(@"C:\Users\User\AppData\Local\Temp\files_bc.txt", DateTime.Now.ToString("o") + " after Logger" + Environment.NewLine); } catch { }
				AppModel = Ioc.Default.GetRequiredService<AppModel>();

				try { System.IO.File.AppendAllText(@"C:\Users\User\AppData\Local\Temp\files_bc.txt", DateTime.Now.ToString("o") + " before window event hooks" + Environment.NewLine); } catch { }
				MainWindow.Instance.Closed += Window_Closed;
				MainWindow.Instance.Activated += Window_Activated;

				try { System.IO.File.AppendAllText(System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "activate_breadcrumb.txt"), DateTime.Now.ToString("o") + " App before splash/main showSplash=" + showSplashScreen + Environment.NewLine); } catch { } // BC_SHOWSPLASH
				if (showSplashScreen)
				{
					SplashScreenLoadingTCS = new TaskCompletionSource();
					MainWindow.Instance.ShowSplashScreen();
				}
				else
				{
					MainWindow.Instance.ShowMainPage();
				}

				try { System.IO.File.AppendAllText(System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "activate_breadcrumb.txt"), DateTime.Now.ToString("o") + " App before ActivateWithBackdropAsync" + Environment.NewLine); } catch { } // BC_ACTIVATE
				if (!await MainWindow.Instance.ActivateWithBackdropAsync())
					return;

				Logger.LogInformation("Startup: initial content rendered after {Elapsed} ms", startup.ElapsedMilliseconds);
				_ = serviceProvider.GetRequiredService<ICommandManager>();

				var telemetryInitialization = AppLifecycleHelper.AppEnvironment is AppEnvironment.Dev
					? Task.CompletedTask
					: Task.Run(AppLifecycleHelper.ConfigureSentry);


				Logger.LogInformation($"App launched. Launch args type: {appActivationArguments.Data.GetType().Name}");

				if (SplashScreenLoadingTCS is not null)
				{
					await SplashScreenLoadingTCS.Task.WithTimeoutAsync(TimeSpan.FromMilliseconds(500));
					SplashScreenLoadingTCS = null;
				}

				_ = SafetyExtensions.IgnoreExceptions(async () => (await serverSession).SetWindowHandle(MainWindow.Instance.WindowHandle), Logger);
				_ = SafetyExtensions.IgnoreExceptions(async () =>
				{
					await serverSession;
					await MainWindow.Instance.InitializeApplicationAsync(appActivationArguments.Data);
				}, Logger);

				await telemetryInitialization;
				await AppLifecycleHelper.InitializeAppComponentsAsync();
			}
		}

		/// <summary>
		/// Gets invoked when the application is activated.
		/// </summary>
		public async Task OnActivatedAsync(AppActivationArguments activatedEventArgs)
		{
			var activatedEventArgsData = activatedEventArgs.Data;

			Logger.LogInformation($"The app is being activated. Activation type: {activatedEventArgsData?.GetType().Name ?? "Unknown"}");

			// InitializeApplication accesses UI, needs to be called on UI thread
			await MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(()
				=> MainWindow.Instance.InitializeApplicationAsync(activatedEventArgsData));
		}

		/// <summary>
		/// Gets invoked when the main window is activated.
		/// </summary>
		private void Window_Activated(object sender, WindowActivatedEventArgs args)
		{
			Logger.LogInformation($"Window_Activated: State={args.WindowActivationState}");

			ActiveSessionTracker.OnActivationChanged(args.WindowActivationState != WindowActivationState.Deactivated);

			if (!_isWindowClosing && args.WindowActivationState != WindowActivationState.Deactivated)
				AppModel.IsMainWindowClosed = false;
		}

		/// <summary>
		/// Gets invoked when the application execution is closed.
		/// </summary>
		/// <remarks>
		/// Saves the current state of the app such as opened tabs, and disposes all cached resources.
		/// </remarks>
		private async void Window_Closed(object sender, WindowEventArgs args)
		{
			if (_isWindowTeardownCompleted)
				return;

			args.Handled = true;
			if (_isWindowClosing)
				return;

			if (_LastOpenedFlyout?.IsOpen ?? false)
			{
				_LastOpenedFlyout.Closed += (_, _) => MainWindow.Instance.Close();
				_LastOpenedFlyout.Hide();
				return;
			}

			_isWindowClosing = true;
			AppModel.IsMainWindowClosed = true;
			if (serverSession.IsCompletedSuccessfully)
				SafetyExtensions.IgnoreExceptions(() => serverSession.Result.SetWindowHandle(0), Logger);
			Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().UnregisterKey();
			ActiveSessionTracker.OnActivationChanged(false);

			try
			{
				FilePropertiesHelpers.DestroyCachedWindows();
				MainWindow.Instance.CloseOtherWindows();

				try { System.IO.File.AppendAllText(@"C:\Users\User\AppData\Local\Temp\files_bc.txt", DateTime.Now.ToString("o") + " before IUserSettingsService" + Environment.NewLine); } catch { }
				var settings = Ioc.Default.GetRequiredService<IUserSettingsService>();
				try { System.IO.File.AppendAllText(@"C:\Users\User\AppData\Local\Temp\files_bc.txt", DateTime.Now.ToString("o") + " after IUserSettingsService" + Environment.NewLine); } catch { }
				var commands = Ioc.Default.GetRequiredService<ICommandManager>();
				if (settings.GeneralSettingsService.ContinueLastSessionOnStartUp || settings.AppSettingsService.RestoreTabsOnStartup)
					AppLifecycleHelper.SaveSessionTabs();
				else
					await commands.CloseAllTabs.ExecuteAsync();

				if (OutputPath is not null)
				{
					var instance = MainPageViewModel.AppInstances.FirstOrDefault(tab => tab.TabItemContent?.IsCurrentInstance == true);
					var items = (instance?.TabItemContent as ShellPanesPage)?.ActivePane?.SlimContentPage?.SelectedItems;
					if (items is not null)
					{
						System.IO.File.WriteAllLines(OutputPath, items.Select(item => item.ItemPath!));
						using var eventHandle = PInvoke.CreateEvent(null, false, false, "FILEDIALOG");
						PInvoke.SetEvent(eventHandle);
					}
				}

				MainWindow.Instance.AppWindow.Hide();
				SafetyExtensions.IgnoreExceptions(() => Ioc.Default.GetRequiredService<InfoPaneViewModel>().UnloadPreview());

				if (settings.GeneralSettingsService.CloseToBackground && settings.AppSettingsService.ShowBackgroundRunningNotification)
				{
					SafetyExtensions.IgnoreExceptions(() =>
					{
						AppToastNotificationHelper.ShowBackgroundRunningToast();
						settings.AppSettingsService.ShowBackgroundRunningNotification = false;
					});
				}

				SafetyExtensions.IgnoreExceptions(() =>
				{
					var dataPackage = Clipboard.GetContent();
					if (dataPackage.Properties.PackageFamilyName == Package.Current.Id.FamilyName && dataPackage.Contains(StandardDataFormats.StorageItems))
						Clipboard.Flush();
				}, Logger);

				await Task.Run(FileOperationsHelpers.WaitForCompletion);
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Failed to finish window teardown.");
			}
			finally
			{
				await SafetyExtensions.IgnoreExceptions(async () => (await serverSession).Dispose(), Logger);
				_isWindowTeardownCompleted = true;
				MainWindow.Instance.Close();
			}
		}

		/// <summary>
		/// Gets invoked when the last opened flyout is closed.
		/// </summary>
		[DynamicWindowsRuntimeCast(typeof(FlyoutBase))]
		private static void LastOpenedFlyout_Closed(object? sender, object e)
		{
			if (sender is not FlyoutBase flyoutBase)
				return;

			flyoutBase.Closed -= LastOpenedFlyout_Closed;
			if (_LastOpenedFlyout == flyoutBase)
				_LastOpenedFlyout = null;
		}
	}
}
