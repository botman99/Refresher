using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.Text;

namespace Refresher
{
	[Export(typeof(IWpfTextViewCreationListener))]
	[ContentType("text")]
	[TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
	internal class TextviewCreationListener : IWpfTextViewCreationListener
	{
		[Import]
		internal ITextDocumentFactoryService TextDocumentFactoryService { get; set; }

		private Dictionary<ITextBuffer, string> OpenDocuments = new Dictionary<ITextBuffer, string>();

		// This method is executed when you open any file in the editor window
		public void TextViewCreated(IWpfTextView textView)
		{
			try
			{
				ITextBuffer buffer = textView.TextDataModel.DocumentBuffer;

				if (TextDocumentFactoryService.TryGetTextDocument(buffer, out ITextDocument document))
				{
					string Filename = document.FilePath;

					OpenDocuments[buffer] = Filename;

					textView.TextBuffer.Changing += OnChanging;
					textView.Closed += OnClosed;
				}
			}
			catch
			{
			}
		}

		private void OnChanging(object sender, TextContentChangingEventArgs e)
		{
			try
			{
				ITextBuffer buffer = sender as ITextBuffer;

				if (buffer != null)
				{
					string Filename = OpenDocuments[buffer];

					lock (RefresherPackage.Refresh_LockObject)
					{
						if (RefresherPackage.RefreshFileList.ContainsKey(Filename))
						{
							if (RefresherPackage.RefreshFileList[Filename] > 0)  // if the refresh is deferred, remove it from the list
							{
								RefresherPackage.RefreshFileList.Remove(Filename);
							}
						}
					}
				}
			}
			catch
			{
			}
		}

		private void OnClosed(object sender, EventArgs e)
		{
			try
			{
				IWpfTextView textView = sender as IWpfTextView;

				if (textView != null)
				{
					ITextBuffer buffer = textView.TextBuffer as ITextBuffer;

					if (buffer != null)
					{
						string Filename = OpenDocuments[buffer];

						lock (RefresherPackage.Refresh_LockObject)
						{
							if (RefresherPackage.RefreshFileList.ContainsKey(Filename))
							{
								RefresherPackage.RefreshFileList.Remove(Filename);
							}
						}
					}

					textView.TextBuffer.Changing -= OnChanging;
					textView.Closed -= OnClosed;
				}
			}
			catch
			{
			}
		}
	}
}
