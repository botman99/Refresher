using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Timers;
using Task = System.Threading.Tasks.Task;

namespace Refresher
{
	/// <summary>
	/// This is the class that implements the package exposed by this assembly.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The minimum requirement for a class to be considered a valid package for Visual Studio
	/// is to implement the IVsPackage interface and register itself with the shell.
	/// This package uses the helper classes defined inside the Managed Package Framework (MPF)
	/// to do it: it derives from the Package class that provides the implementation of the
	/// IVsPackage interface and uses the registration attributes defined in the framework to
	/// register itself and its components with the shell. These attributes tell the pkgdef creation
	/// utility what data to put into .pkgdef file.
	/// </para>
	/// <para>
	/// To get loaded into VS, the package must be referred by &lt;Asset Type="Microsoft.VisualStudio.VsPackage" ...&gt; in .vsixmanifest file.
	/// </para>
	/// </remarks>
	[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
	[ProvideAutoLoad(UIContextGuids80.NoSolution,	PackageAutoLoadFlags.BackgroundLoad)]
	[ProvideAutoLoad(UIContextGuids80.EmptySolution,	PackageAutoLoadFlags.BackgroundLoad)]
	[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionOpening_string,	PackageAutoLoadFlags.BackgroundLoad)]
	[Guid(RefresherPackage.PackageGuidString)]
	public sealed class RefresherPackage : AsyncPackage, IVsSolutionEvents, IVsSolutionEvents7
	{
		/// <summary>
		/// RefresherPackage GUID string.
		/// </summary>
		public const string PackageGuidString = "14f18831-e71c-4681-9cb5-7124e25adf48";

		// The cookie for solution events
		private uint _vsSolutionEventsCookie;

		private System.Timers.Timer CheckEditorWindowsTimer = new System.Timers.Timer();

		private DTE dte;
		private DocumentEvents documentEvents;

		// these are watchers to track changes to files (writes, read-only, deletes, adds/renames)
		private Dictionary<string, FileSystemWatcher> lastwrite_watchers = new Dictionary<string, FileSystemWatcher>();
		private Dictionary<string, FileSystemWatcher> directory_watchers = new Dictionary<string, FileSystemWatcher>();

		private HashSet<string> document_files = new HashSet<string>();  // this is the collection of open documents

		private System.Timers.Timer RefreshTimer = new System.Timers.Timer();

		private object Refresh_LockObject = new Object();
		private Dictionary<string, long> RefreshFileList = new Dictionary<string, long>();

		private object Delete_LockObject = new Object();
		private Dictionary<string, long> DeleteFileList = new Dictionary<string, long>();

		#region Package Members

		/// <summary>
		/// Initialization of the package; this method is called right after the package is sited, so this is the place
		/// where you can put all the initialization code that rely on services provided by VisualStudio.
		/// </summary>
		/// <param name="cancellationToken">A cancellation token to monitor for initialization cancellation, which can occur when VS is shutting down.</param>
		/// <param name="progress">A provider for progress updates.</param>
		/// <returns>A task representing the async work of package initialization, or an already completed task if there is none. Do not return null from this method.</returns>
		protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
		{
			// When initialized asynchronously, the current thread may be a background thread at this point.
			// Do any initialization that requires the UI thread after switching to the UI thread.
			await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

			// Subscribe to solution events (so we can detect when the solution/folder is loaded)
			IVsSolution sol = (IVsSolution)GetGlobalService(typeof(SVsSolution));
			sol.AdviseSolutionEvents(this, out _vsSolutionEventsCookie);

			dte = Package.GetGlobalService(typeof(DTE)) as DTE;
			if (dte != null)
			{
				documentEvents = dte.Events.DocumentEvents;
				documentEvents.DocumentOpened += OnDocumentOpened;
				documentEvents.DocumentClosing += OnDocumentClosing;
			}

			// When the Solution/Folder is loaded, files that were previously opened in the editor will be opened, but not loaded.
			// The OnDocumentOpened callback will only be called for the editor window with focus.  It will get the file watcher
			// created, but the other editor windows won't get a file watcher until you click on them to force the file to load.
			// This timer keeps checking for any open editor windows (whether active or not) and will
			// create the file watcher for the files in those unfocused windows.
			CheckEditorWindowsTimer.Interval = 1000;
			CheckEditorWindowsTimer.AutoReset = true;
			CheckEditorWindowsTimer.Elapsed += OnEditorWindowsTimerEvent;
			CheckEditorWindowsTimer.Enabled = true;

			RefreshTimer.Interval = 50;
			RefreshTimer.AutoReset = true;
			RefreshTimer.Elapsed += OnRefreshTimerEvent;
			RefreshTimer.Enabled = true;
		}

		private void OnEditorWindowsTimerEvent(object sender, ElapsedEventArgs e)
		{
			CheckEditorWindowsAsync().GetAwaiter();
		}

		async Task CheckEditorWindowsAsync()
		{
			// UI Shell access must be done on the UI thread, so we need to switch to it here
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

			try
			{
				List<string> EditorFiles = new List<string>();

				// Get the UI Shell service
				if (ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell)) is IVsUIShell uiShell)
				{
					// Get an enumerator for all window frames
					if (uiShell.GetDocumentWindowEnum(out IEnumWindowFrames windowFramesEnum) == VSConstants.S_OK)
					{
						IVsWindowFrame[] frameArray = new IVsWindowFrame[1];
						uint fetched;

						// Loop through all open document tabs
						while (windowFramesEnum.Next(1, frameArray, out fetched) == VSConstants.S_OK && fetched == 1)
						{
							IVsWindowFrame frame = frameArray[0];

							if (frame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out object docPathObj) == VSConstants.S_OK)
							{
								if (docPathObj is string filePath)
								{
									EditorFiles.Add(filePath);
								}
							}
						}
					}
				}

				if (EditorFiles.Count > 0)
				{
					foreach (string file in EditorFiles)
					{
						document_files.Add(file);

						AddWatchers(file);
					}
				}
			}
			catch
			{
			}
		}

		private void OnRefreshTimerEvent(object sender, ElapsedEventArgs e)
		{
			try
			{
				lock (Refresh_LockObject)
				{
					long TimeNow = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

					if (RefreshFileList.Count > 0)
					{
						foreach (var refresh in RefreshFileList)
						{
							if ((refresh.Value == 0) || (refresh.Value < TimeNow))
							{
								string Filename = refresh.Key;
								RefreshDocumentAsync(Filename).GetAwaiter();

								RefreshFileList.Remove(Filename);
							}
						}
					}

					if (DeleteFileList.Count > 0)
					{
						foreach (var refresh in DeleteFileList)
						{
							if ((refresh.Value == 0) || (refresh.Value < TimeNow))
							{
								string Filename = refresh.Key;
								CloseDocumentAsync(Filename).GetAwaiter();

								DeleteFileList.Remove(Filename);
							}
						}
					}
				}
			}
			catch
			{
			}
		}

		async Task RefreshDocumentAsync(string Filename)
		{
			// RunningDocumentTable access must be done on the UI thread, so we need to switch to it here
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

			try
			{
				if (!File.Exists(Filename))
				{
					return;
				}

				var rdt = (IVsRunningDocumentTable)ServiceProvider.GlobalProvider.GetService(typeof(SVsRunningDocumentTable));

				if (rdt != null)
				{
					int hr = rdt.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, Filename, out IVsHierarchy hierarchy, out uint itemId, out IntPtr docDataPtr, out uint cookie);

					if ((hr == VSConstants.S_OK) && (docDataPtr != IntPtr.Zero))
					{
						try
						{
							object docData = Marshal.GetObjectForIUnknown(docDataPtr);
							var persistDocData = docData as IVsPersistDocData;

							if (persistDocData.IsDocDataDirty(out int pfDirty) == VSConstants.S_OK)
							{
								if (pfDirty == 1)
								{
									string message = $"{Filename}\n\nThe file has unsaved changes inside this editor and has been\nchanged externally  Do you want to reload it and lose the\nchanges made in the source code editor?";
									int result = VsShellUtilities.ShowMessageBox(this, message, "Refresher", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

									if (result == (int)VSConstants.MessageBoxResult.IDNO)
									{
										Marshal.Release(docDataPtr);

										return;
									}
								}
							}

							if (persistDocData != null)
							{
								hr = persistDocData.ReloadDocData(0);
							}
						}
						catch
						{
						}
						finally
						{
							Marshal.Release(docDataPtr);
						}
					}
				}
			}
			catch
			{
			}
		}

		async Task CloseDocumentAsync(string Filename)
		{
			// RunningDocumentTable access must be done on the UI thread, so we need to switch to it here
			await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

			try
			{
				var rdt = (IVsRunningDocumentTable)ServiceProvider.GlobalProvider.GetService(typeof(SVsRunningDocumentTable));
				var uiShellOpenDoc = (IVsUIShellOpenDocument)ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShellOpenDocument));

				if ((rdt != null) && (uiShellOpenDoc != null))
				{
					int hr = rdt.FindAndLockDocument((uint)_VSRDTFLAGS.RDT_NoLock, Filename, out IVsHierarchy hierarchy, out uint itemId, out IntPtr docDataPtr, out uint cookie);

					if ((hr == VSConstants.S_OK) && (docDataPtr != IntPtr.Zero) && (cookie != 0))
					{
						Guid logicalView = VSConstants.LOGVIEWID_Primary;
						uint[] pitemidOpen = new uint[1];

						hr = uiShellOpenDoc.IsDocumentOpen(hierarchy as IVsUIHierarchy, itemId, Filename, ref logicalView, (uint)__VSIDOFLAGS.IDO_IgnoreLogicalView, out IVsUIHierarchy ppHierOpen, pitemidOpen, out IVsWindowFrame windowFrame, out int isOpen);

						if ((hr == VSConstants.S_OK) && (isOpen != 0) && (windowFrame != null))
						{
							windowFrame.CloseFrame((uint)__FRAMECLOSE.FRAMECLOSE_PromptSave);
						}
					}
				}
			}
			catch
			{
			}
		}

		private void OnDocumentOpened(Document document)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			try
			{
				document_files.Add(document.FullName);

				AddWatchers(document.FullName);
			}
			catch
			{
			}
		}

		private void OnDocumentClosing(Document document)
		{
			ThreadHelper.ThrowIfNotOnUIThread();

			try
			{
				if (document_files.Contains(document.FullName))
				{
					document_files.Remove(document.FullName);
				}

				RemoveWatchers(document.FullName);
			}
			catch
			{
			}
		}

		void AddWatchers(string filepath)
		{
			try
			{
				if (!lastwrite_watchers.ContainsKey(filepath))
				{
					string directory = Path.GetDirectoryName(filepath);
					string fileName = Path.GetFileName(filepath);

					FileSystemWatcher watcher = null;

					watcher = new FileSystemWatcher(directory, fileName);
					watcher.NotifyFilter = NotifyFilters.LastWrite;

					watcher.Changed += OnWatcherLastWriteEvent;
					watcher.Created += OnWatcherLastWriteEvent;
					watcher.Deleted += OnWatcherLastWriteEvent;
					watcher.Renamed += OnWatcherLastWriteEvent;
					watcher.EnableRaisingEvents = true;

					lastwrite_watchers.Add(filepath, watcher);

					if (!directory_watchers.ContainsKey(directory))
					{
						watcher = new FileSystemWatcher(directory, "*.*");
						watcher.NotifyFilter = NotifyFilters.FileName;

						watcher.Changed += (sender, e) => { };
						watcher.Created += OnWatcherDirectoryEvent;
						watcher.Deleted += OnWatcherDirectoryEvent;
						watcher.Renamed += OnWatcherDirectoryEvent;
						watcher.EnableRaisingEvents = true;

						directory_watchers.Add(directory, watcher);
					}
				}
			}
			catch
			{
			}
		}

		void RemoveWatchers(string filepath)
		{
			try
			{
				if (lastwrite_watchers.ContainsKey(filepath))
				{
					lastwrite_watchers[filepath].EnableRaisingEvents = false;
					lastwrite_watchers[filepath].Dispose();
					lastwrite_watchers.Remove(filepath);

					// build a collection of all the directories we are still watching
					HashSet<string> document_directories = new HashSet<string>();

					foreach(string document_path in document_files)
					{
						string directory = Path.GetDirectoryName(document_path);
						document_directories.Add(directory);
					}

					// go through all directory watchers and keep track of any not in the document_directories
					HashSet<string> to_remove = new HashSet<string>();

					foreach(var directory in directory_watchers)
					{
						if (!document_directories.Contains(directory.Key))
						{
							to_remove.Add(directory.Key);
						}
					}

					// remove any directory watchers that don't have any document files
					foreach (string directory in to_remove)
					{
						directory_watchers[directory].EnableRaisingEvents = false;
						directory_watchers[directory].Dispose();

						directory_watchers.Remove(directory);
					}
				}
			}
			catch
			{
			}
		}

		private void RemoveAllWatchers()
		{
			try
			{
				document_files.Clear();

				lock (Refresh_LockObject)
				{
					RefreshFileList.Clear();
				}

				lock (Delete_LockObject)
				{
					DeleteFileList.Clear();
				}

				foreach (var watcher in lastwrite_watchers)
				{
					watcher.Value.EnableRaisingEvents = false;
					watcher.Value.Dispose();
				}

				lastwrite_watchers.Clear();

				foreach (var watcher in directory_watchers)
				{
					watcher.Value.EnableRaisingEvents = false;
					watcher.Value.Dispose();
				}

				directory_watchers.Clear();
			}
			catch
			{
			}
		}

		private void OnWatcherLastWriteEvent(object sender, FileSystemEventArgs args)
		{
			try
			{
				lock (Refresh_LockObject)
				{
					if (!RefreshFileList.ContainsKey(args.FullPath))
					{
						RefreshFileList.Add(args.FullPath, 0);
					}
				}
			}
			catch
			{
			}
		}

		private void OnWatcherDirectoryEvent(object sender, FileSystemEventArgs args)
		{
			try
			{
				if (args.ChangeType == WatcherChangeTypes.Deleted)
				{
					if (document_files.Contains(args.FullPath))
					{
						lock (Delete_LockObject)
						{
							long DeleteTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 500;  // defer Refresh by half a second

							if (!DeleteFileList.ContainsKey(args.FullPath))
							{
								DeleteFileList.Add(args.FullPath, DeleteTime);
							}
						}
					}
				}
				else if (args.ChangeType == WatcherChangeTypes.Renamed)
				{
					if (document_files.Contains(args.FullPath))
					{
						lock (Delete_LockObject)
						{
							// see if the renamed file was recently deleted...
							if (DeleteFileList.ContainsKey(args.FullPath))
							{
								DeleteFileList.Remove(args.FullPath);  // don't delete it

								// this file was deleted and then a temp file was renamed to the deleted file
								lock (Refresh_LockObject)
								{
									if (!RefreshFileList.ContainsKey(args.FullPath))
									{
										RefreshFileList.Add(args.FullPath, 0);
									}
								}
							}
						}
					}
				}
			}
			catch
			{
			}
		}

		#endregion

		//----------------------------------------------------------------------------
		// IVsSolutionEvents specific functions
		//--------------------------------------------------------------------------------
		#region IVsSolutionEvents interface functions

		public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded)
		{
			return VSConstants.S_OK;
		}

		public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel)
		{
			return VSConstants.S_OK;
		}

		public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved)
		{
			return VSConstants.S_OK;
		}

		public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy)
		{
			return VSConstants.S_OK;
		}

		public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel)
		{
			return VSConstants.S_OK;
		}

		public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy)
		{
			return VSConstants.S_OK;
		}

		public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
		{
			return VSConstants.S_OK;
		}

		public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel)
		{
			return VSConstants.S_OK;
		}

		public int OnBeforeCloseSolution(object pUnkReserved)
		{
			CheckEditorWindowsTimer.Enabled = false;

			return VSConstants.S_OK;
		}

		public int OnAfterCloseSolution(object pUnkReserved)
		{
			RemoveAllWatchers();

			return VSConstants.S_OK;
		}

		#endregion  // IVsSolutionEvents

		//----------------------------------------------------------------------------
		// IVsSolutionEvents7 specific functions
		//--------------------------------------------------------------------------------
		#region IVsSolutionEvents7 interface functions
		public void OnAfterOpenFolder(string folderPath)
		{
		}

		public void OnQueryCloseFolder(string folderPath, ref int pfCancel)
		{
		}

		public void OnBeforeCloseFolder(string folderPath)
		{
			CheckEditorWindowsTimer.Enabled = false;
		}

		public void OnAfterCloseFolder(string folderPath)
		{
			RemoveAllWatchers();
		}

		[Obsolete("This API is no longer supported by Visual Studio.")]
		public void OnAfterLoadAllDeferredProjects()
		{ }

		#endregion // IVsSolutionEvents7                       

	}
}
