// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using WinRT;
using DragEventArgs = Microsoft.UI.Xaml.DragEventArgs;

namespace Files.App.UserControls
{
	/// <summary>
	/// Represents a horizontal bookmarks bar shown below the address bar.
	/// </summary>
	public sealed partial class BookmarksBar : UserControl
	{
		private IContentPageContext ContentPageContext { get; } = Ioc.Default.GetRequiredService<IContentPageContext>();
		private IQuickAccessService QuickAccessService { get; } = Ioc.Default.GetRequiredService<IQuickAccessService>();

		/// <summary>
		/// Bookmark chip that opened the shared context flyout (Target/DataContext can be unreliable).
		/// </summary>
		private LocationItem? _flyoutItem;

		[DynamicWindowsRuntimeCast(typeof(MenuFlyout))]
		private MenuFlyout? GetBookmarkFlyout() => Resources["BookmarkFlyout"] as MenuFlyout;

		[DynamicWindowsRuntimeCast(typeof(MenuFlyout))]
		private MenuFlyout? GetEmptyBarFlyout() => Resources["EmptyBarFlyout"] as MenuFlyout;

		public BookmarksBarViewModel ViewModel { get; }

		public BookmarksBar()
		{
			ViewModel = Ioc.Default.GetRequiredService<BookmarksBarViewModel>();
			InitializeComponent();

			ViewModel.Items.CollectionChanged += Items_CollectionChanged;
			Loaded += BookmarksBar_Loaded;

			UpdateEmptyHint();
		}

		private void BookmarksBar_Loaded(object sender, RoutedEventArgs e)
			=> _ = ViewModel.RefreshAsync();

		private void Items_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
			=> UpdateEmptyHint();

		private void UpdateEmptyHint()
		{
			if (EmptyHint is null)
				return;

			EmptyHint.Visibility = ViewModel.Items.Count is 0 ? Visibility.Visible : Visibility.Collapsed;
		}

		
		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		[DynamicWindowsRuntimeCast(typeof(DependencyObject))]
		private static LocationItem? FindLocationItemFromVisual(DependencyObject? start)
		{
			for (var current = start; current is not null; current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
			{
				if (current is FrameworkElement fe)
				{
					if (fe.Tag is LocationItem tagged)
						return tagged;
					if (fe.DataContext is LocationItem fromContext)
						return fromContext;
				}
			}

			return null;
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private static LocationItem? GetItem(object? sender)
		{
			if (sender is not FrameworkElement element)
				return null;

			// x:Bind DataTemplates often leave DataContext null; Tag is bound to the LocationItem.
			if (element.Tag is LocationItem tagged)
				return tagged;

			if (element.DataContext is LocationItem fromContext)
				return fromContext;

			return null;
		}

		private LocationItem? GetFlyoutItem()
			=> _flyoutItem ?? GetItem(GetBookmarkFlyout()?.Target);

		/// <summary>
		/// Left-click opens the bookmark in a new tab (file-manager friendly; Chrome opens in-place).
		/// Shift+click opens in a new window. Middle-click also opens a new tab.
		/// </summary>
		private async void Bookmark_Click(object sender, RoutedEventArgs e)
		{
			if (GetItem(sender) is not { Path: { } path })
				return;

			if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down))
			{
				await NavigationHelpers.OpenPathInNewWindowAsync(path);
				return;
			}

			// Always switch so the click feels like navigation (primary action).
			await NavigationHelpers.OpenPathInNewTab(path, true);
		}

		private async void Bookmark_PointerPressed(object sender, PointerRoutedEventArgs e)
		{
			if (!e.GetCurrentPoint(null).Properties.IsMiddleButtonPressed)
				return;

			if (GetItem(sender) is { Path: { } path })
				await NavigationHelpers.OpenPathInNewTab(path, true);
		}

		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		private void Bookmark_RightTapped(object sender, RightTappedRoutedEventArgs e)
		{
			if (sender is not FrameworkElement element || GetItem(sender) is not { } item || GetBookmarkFlyout() is not { } flyout)
				return;

			_flyoutItem = item;

			if (BookmarkFlyoutAddBookmarkItem is not null)
				BookmarkFlyoutAddBookmarkItem.IsEnabled = CanPinCurrentOrSelectedFolders();

			flyout.ShowAt(element, new FlyoutShowOptions() { Position = e.GetPosition(element) });
			e.Handled = true;
		}

		private async void OpenInCurrentTab_Click(object sender, RoutedEventArgs e)
		{
			if (GetFlyoutItem() is not { Path: { } path } || ContentPageContext.ShellPage is not { } shellPage)
				return;

			shellPage.NavigateWithArguments(
				shellPage.InstanceViewModel.FolderSettings.GetLayoutType(path),
				new NavigationArguments() { NavPathParam = path });
		}

		private async void OpenInNewTab_Click(object sender, RoutedEventArgs e)
		{
			if (GetFlyoutItem() is { Path: { } path })
				await NavigationHelpers.OpenPathInNewTab(path, true);
		}

		private async void OpenInNewWindow_Click(object sender, RoutedEventArgs e)
		{
			if (GetFlyoutItem() is { Path: { } path })
				await NavigationHelpers.OpenPathInNewWindowAsync(path);
		}

		private async void Unpin_Click(object sender, RoutedEventArgs e)
		{
			if (GetFlyoutItem() is { Path: { } path })
				await QuickAccessService.UnpinFromSidebarAsync(path);
		}

		/// <summary>
		/// Opens the sidebar expanded with the pinned section visible — closest Files-native "manage bookmarks".
		/// </summary>
		private void ManageBookmarks_Click(object sender, RoutedEventArgs e)
		{
			var sidebar = Ioc.Default.GetRequiredService<SidebarViewModel>();
			sidebar.ShowPinnedFoldersSection = true;
			sidebar.SidebarDisplayMode = SidebarDisplayMode.Expanded;

			var mainPage = Ioc.Default.GetService<MainPageViewModel>();
			if (mainPage is not null)
				mainPage.IsSidebarPaneOpen = true;
		}

		/// <summary>
		/// Creates a folder under Documents\Files Bookmarks and pins it.
		/// Windows Quick Access pins are flat — this is the closest stand-in for Chrome bookmark folders.
		/// </summary>
		private async void AddFolder_Click(object sender, RoutedEventArgs e)
		{
			var dialog = DynamicDialogFactory.GetFor_CreateItemDialog("Folder", null);
			var result = await DialogDisplayHelper.ShowDialogAsync(dialog);
			if (result is not DynamicDialogResult.Primary)
				return;

			if (dialog.ViewModel.AdditionalData is not string name || string.IsNullOrWhiteSpace(name))
				return;

			name = FilesystemHelpers.FilterRestrictedCharacters(name.Trim());
			if (string.IsNullOrWhiteSpace(name) || !FilesystemHelpers.IsValidForFilename(name))
				return;

			var root = SystemIO.Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
				"Files Bookmarks");

			try
			{
				SystemIO.Directory.CreateDirectory(root);
				var path = SystemIO.Path.Combine(root, name);
				SystemIO.Directory.CreateDirectory(path);
				await QuickAccessService.PinToSidebarAsync([path]);
			}
			catch (Exception ex)
			{
				App.Logger?.LogWarning(ex, "Failed to create bookmarks-bar folder category");
				await DialogDisplayHelper.ShowDialogAsync(
					Strings.ErrorDialogUnsupportedOperation.GetLocalizedResource(),
					ex.Message);
			}
		}

		/// <summary>
		/// Accept folder drops onto the bar (Clover-style pin).
		/// Uses Move|Copy|Link so Explorer and Files-list payloads (which rarely advertise Link alone) still drop.
		/// </summary>
		private async void BookmarksBar_DragOver(object sender, DragEventArgs e)
		{
			var deferral = e.GetDeferral();
			try
			{
				var canPin = await CanPinDraggedFoldersAsync(e.DataView);
				e.Handled = true;

				if (!canPin)
				{
					e.AcceptedOperation = DataPackageOperation.None;
					return;
				}

				// Match sidebar pin: Move is what Files advertises for reorder/pin; Copy covers Explorer.
				e.AcceptedOperation = DataPackageOperation.Move | DataPackageOperation.Copy | DataPackageOperation.Link;
				e.DragUIOverride.IsCaptionVisible = true;
				e.DragUIOverride.Caption = Strings.PinFolderToSidebar.GetLocalizedResource();
			}
			finally
			{
				deferral.Complete();
			}
		}

		/// <summary>
		/// Pin dropped folders via the same Quick Access API used by the sidebar.
		/// </summary>
		private async void BookmarksBar_Drop(object sender, DragEventArgs e)
		{
			var deferral = e.GetDeferral();
			try
			{
				e.Handled = true;

				var foldersToPin = await GetFoldersToPinFromDragAsync(e.DataView);
				if (foldersToPin.Length is 0)
					return;

				await QuickAccessService.PinToSidebarAsync(foldersToPin);
			}
			finally
			{
				deferral.Complete();
			}
		}

		/// <summary>
		/// Empty-area context menu: pin the current folder / selection, add folder, manage.
		/// </summary>
		[DynamicWindowsRuntimeCast(typeof(FrameworkElement))]
		[DynamicWindowsRuntimeCast(typeof(DependencyObject))]
		private void RootGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
		{
			// Bookmark chips handle their own menu and mark Handled.
			if (e.Handled || GetEmptyBarFlyout() is not { } flyout)
				return;

			// Ignore right-clicks that landed on a bookmark chip (Tag/DataContext is LocationItem).
			if (FindLocationItemFromVisual((e.OriginalSource as DependencyObject)) is not null)
				return;

			_flyoutItem = null;

			if (PinCurrentFolderItem is not null)
				PinCurrentFolderItem.IsEnabled = CanPinCurrentOrSelectedFolders();

			flyout.ShowAt(RootGrid, new FlyoutShowOptions() { Position = e.GetPosition(RootGrid) });
			e.Handled = true;
		}

		private async void PinCurrentFolder_Click(object sender, RoutedEventArgs e)
		{
			var paths = GetFoldersToPinFromContext();
			if (paths.Length is 0)
				return;

			await QuickAccessService.PinToSidebarAsync(paths);
		}

		private bool CanPinCurrentOrSelectedFolders()
			=> GetFoldersToPinFromContext().Length > 0;

		private string[] GetFoldersToPinFromContext()
		{
			string[] pinned = [.. App.QuickAccessManager.Model.PinnedFolders];

			if (ContentPageContext.HasSelection)
			{
				return ContentPageContext.SelectedItems
					.Where(IsPinnableListedItem)
					.Select(item => item.ItemPath!)
					.Where(path => !pinned.Contains(path, StringComparer.OrdinalIgnoreCase))
					.ToArray();
			}

			if (ContentPageContext.Folder is { } folder && IsPinnableListedItem(folder) &&
				!pinned.Contains(folder.ItemPath!, StringComparer.OrdinalIgnoreCase))
			{
				return [folder.ItemPath!];
			}

			return [];
		}

		private async Task<bool> CanPinDraggedFoldersAsync(DataPackageView dataView)
			=> (await GetFoldersToPinFromDragAsync(dataView)).Length > 0;

		/// <summary>
		/// Resolves pinnable folder paths from Explorer StorageItems/FileDrop, Files Shell IDList,
		/// plain text paths, or (for in-app Shell IDList drags) the current folder selection.
		/// </summary>
		private async Task<string[]> GetFoldersToPinFromDragAsync(DataPackageView dataView)
		{
			string[] pinned = [.. App.QuickAccessManager.Model.PinnedFolders];
			var results = new List<string>();

			if (FilesystemHelpers.HasDraggedStorageItems(dataView))
			{
				var storageItems = await FilesystemHelpers.GetDraggedStorageItems(dataView);
				results.AddRange(
					storageItems
						.Where(item =>
							item.ItemType is FilesystemItemType.Directory &&
							!string.IsNullOrEmpty(item.Path) &&
							item.Item is not ZipStorageFolder &&
							!ZipStorageFolder.IsZipPath(item.Path))
						.Select(item => item.Path));
			}

			// Files-list drag often only packages "Shell IDList Array" (see BaseLayoutPage).
			if (results.Count is 0 && dataView.Contains(ShellDataObject.ShellIdListArrayFormat))
			{
				results.AddRange(
					ContentPageContext.SelectedItems
						.Where(IsPinnableListedItem)
						.Select(item => item.ItemPath!));
			}

			// Sidebar-style text path payload
			if (results.Count is 0 && dataView.Contains(StandardDataFormats.Text))
			{
				var text = await SafetyExtensions.IgnoreExceptions(
					async () => await dataView.GetTextAsync(),
					App.Logger);

				if (!string.IsNullOrWhiteSpace(text))
				{
					var path = text.Trim().Trim('"');
					if (IsPinnablePath(path))
						results.Add(path);
				}
			}

			return results
				.Where(path => !string.IsNullOrEmpty(path) && !pinned.Contains(path, StringComparer.OrdinalIgnoreCase))
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToArray();
		}

		private static bool IsPinnableListedItem(ListedItem item)
		{
			return
				item.PrimaryItemAttribute is StorageItemTypes.Folder &&
				!item.IsArchive &&
				!string.IsNullOrEmpty(item.ItemPath);
		}

		private static bool IsPinnablePath(string path)
		{
			if (string.IsNullOrWhiteSpace(path) || ZipStorageFolder.IsZipPath(path))
				return false;

			if (ShellStorageFolder.IsShellPath(path))
				return true;

			return SystemIO.Directory.Exists(path);
		}
	}
}
