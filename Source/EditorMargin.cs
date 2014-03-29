﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

using AlekseyNagovitsyn.TfsPendingChangesMargin.Settings;

using EnvDTE;
using EnvDTE80;

using Microsoft.TeamFoundation;
using Microsoft.TeamFoundation.Client;
using Microsoft.TeamFoundation.VersionControl.Client;
using Microsoft.TeamFoundation.VersionControl.Common;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.TeamFoundation;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Outlining;

namespace AlekseyNagovitsyn.TfsPendingChangesMargin
{
    /// <summary>
    /// A class detailing the margin's visual definition including both size and content.
    /// </summary>
    internal class EditorMargin : Canvas, IWpfTextViewMargin
    {
        #region Fields

        /// <summary>
        /// The name of this margin.
        /// </summary>
        internal const string MarginName = "TfsPendingChangesMargin";

        /// <summary>
        /// The current instance of <see cref="IWpfTextView"/>.
        /// </summary>
        private readonly IWpfTextView _textView;

        /// <summary>
        /// The current instance of <see cref="ITextDocument"/> which is associated with <see cref="_textView"/>.
        /// </summary>
        private readonly ITextDocument _textDoc;

        /// <summary>
        /// Visual Studio service provider.
        /// </summary>
// ReSharper disable once NotAccessedField.Local
        private readonly SVsServiceProvider _vsServiceProvider;

        /// <summary>
        /// Map that contains properties from "Tools -> Environment -> Fonts and Colors" dialog in Visual Studio.
        /// </summary>
        private readonly IEditorFormatMap _formatMap;

        /// <summary>
        /// Provides outlining functionality.
        /// </summary>
        private readonly IOutliningManager _outliningManager;

        /// <summary>
        /// The top-level object in the Visual Studio automation object model.
        /// </summary>
// ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
        private readonly DTE2 _dte;

        /// <summary>
        /// Represents the Team Foundation Server extensibility model within Visual Studio.
        /// </summary>
        private readonly TeamFoundationServerExt _tfExt;

        /// <summary>
        /// The margin settings.
        /// </summary>
        private readonly MarginSettings _marginSettings;

        /// <summary>
        /// Lock-object used to synchronize <see cref="GetDifference"/> method.
        /// </summary>
        private readonly object _differenceLockObject = new object();

        /// <summary>
        /// Task for observation over VersionControlItem up-dating.
        /// </summary>
        private readonly Task _versionControlItemWatcher;

        /// <summary>
        /// The margin has been disposed of.
        /// </summary>
        private bool _isDisposed;

        /// <summary>
        /// The margin is enabled.
        /// </summary>
        private bool _isEnabled;

        /// <summary>
        /// Represents the version control repository.
        /// </summary>
        private VersionControlServer _versionControl;

        /// <summary>
        /// Represents a committed version of the <see cref="_textDoc"/> in the version control server.
        /// </summary>
        private Item _versionControlItem;

        /// <summary>
        /// Content of the commited version of the <see cref="_textDoc"/> in the version control server.
        /// </summary>
        private Stream _versionControlItemStream;

        /// <summary>
        /// Determines whether <see cref="_versionControlItemWatcher"/> is cancelled.
        /// </summary>
        private bool _versionControlItemWatcherCancelled;

        /// <summary>
        /// Collection that contains the result of comparing the document's local file with his source control version.
        /// <para/>Each element is a pair of key and value: the key is a line number, the value is a type of difference.
        /// </summary>
        private Dictionary<int, LineDiffType> _cachedChangedLines = new Dictionary<int, LineDiffType>();

        #endregion Fields

        #region IWpfTextViewMargin Members

        /// <summary>
        /// The <see cref="System.Windows.FrameworkElement"/> that implements the visual representation of the margin.
        /// Since this margin implements <see cref="System.Windows.Controls.Canvas"/>, this is the object which renders the margin.
        /// </summary>
        public FrameworkElement VisualElement
        {
            get
            {
                ThrowIfDisposed();
                return this;
            }
        }

        /// <summary>
        /// Since this is a vertical margin, its height will be bound to the height of the text view. 
        /// </summary>
        public double MarginSize
        {

            get
            {
                ThrowIfDisposed();
                return ActualHeight;
            }
        }

        /// <summary>
        /// Determines whether the margin is enabled.
        /// </summary>
        public bool Enabled
        {
            get
            {
                ThrowIfDisposed();
                return _isEnabled;
            }
        }

        /// <summary>
        /// Returns an instance of the margin if this is the margin that has been requested.
        /// </summary>
        /// <param name="marginName">The name of the margin requested.</param>
        /// <returns>An instance of <see cref="EditorMargin"/> or <c>null</c>.</returns>
        public ITextViewMargin GetTextViewMargin(string marginName)
        {
            return (marginName == MarginName) ? this : null;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            if (!_isDisposed)
            {
                SetMarginEnabled(false);
                _isDisposed = true;
            }
        }

        #endregion IWpfTextViewMargin Members

        /// <summary>
        /// Creates a <see cref="EditorMargin"/> for a given <see cref="IWpfTextView"/>.
        /// </summary>
        /// <param name="textView">The <see cref="IWpfTextView"/> to attach the margin to.</param>
        /// <param name="textDocumentFactoryService">Service that creates, loads, and disposes text documents.</param>
        /// <param name="vsServiceProvider">Visual Studio service provider.</param>
        /// <param name="outliningManagerService">Service that provides the <see cref="IOutliningManager"/>.</param>
        /// <param name="formatMapService">Service that provides the <see cref="IEditorFormatMap"/>.</param>
        public EditorMargin(
            IWpfTextView textView, 
            ITextDocumentFactoryService textDocumentFactoryService, 
            SVsServiceProvider vsServiceProvider, 
            IOutliningManagerService outliningManagerService, 
            IEditorFormatMapService formatMapService)
        {
            Debug.WriteLine("Entering constructor.", MarginName);

            _textView = textView;
            if (!textDocumentFactoryService.TryGetTextDocument(_textView.TextBuffer, out _textDoc))
            {
                Debug.WriteLine("Can not retrieve TextDocument. Margin is disabled.", MarginName);
                return;
            }

            InitializeView();

            _vsServiceProvider = vsServiceProvider;
            _outliningManager = outliningManagerService.GetOutliningManager(_textView);
            _formatMap = formatMapService.GetEditorFormatMap(textView);
            _marginSettings = new MarginSettings(_formatMap);
            _dte = (DTE2)vsServiceProvider.GetService(typeof(DTE));

            _tfExt = _dte.GetObject(typeof(TeamFoundationServerExt).FullName);
            Debug.Assert(_tfExt != null, "_tfExt is null.");
            _tfExt.ProjectContextChanged += (s, e) => UpdateMargin();

            _versionControlItemWatcher = new Task(ObserveVersionControlItem);

            UpdateMargin();
        }

        /// <summary>
        /// Initialize view.
        /// </summary>
        private void InitializeView()
        {
            // A little indent before the outline margin.
            Margin = new Thickness(0, 0, 2, 0);
            Width = 5;
            ClipToBounds = true;
        }

        /// <summary>
        /// Sets the enabled state of the margin.
        /// </summary>
        /// <param name="marginEnabled">The new enabled state to assign to the margin.</param>
        private void SetMarginEnabled(bool marginEnabled)
        {
            if (_isEnabled == marginEnabled)
                return;

            _isEnabled = marginEnabled;
            Debug.WriteLine(string.Format("MarginEnabled: {0} ({1})", marginEnabled, _textDoc.FilePath), MarginName);

            if (marginEnabled)
            {
                _textView.LayoutChanged += OnTextViewLayoutChanged;
                _textView.ZoomLevelChanged += OnTextViewZoomLevelChanged;
                _textDoc.FileActionOccurred += OnTextDocFileActionOccurred;
                _formatMap.FormatMappingChanged += OnFormatMapFormatMappingChanged;
                _versionControl.CommitCheckin += OnVersionControlCommitCheckin;

                _versionControlItemWatcherCancelled = false;
                if (_versionControlItemWatcher.Status != TaskStatus.Running)
                    _versionControlItemWatcher.Start(TaskScheduler.Default);
            }
            else
            {
                _textView.LayoutChanged -= OnTextViewLayoutChanged;
                _textView.ZoomLevelChanged -= OnTextViewZoomLevelChanged;
                _textDoc.FileActionOccurred -= OnTextDocFileActionOccurred;
                _formatMap.FormatMappingChanged -= OnFormatMapFormatMappingChanged;
                _versionControl.CommitCheckin -= OnVersionControlCommitCheckin;

                _versionControlItemWatcherCancelled = true;
            }
        }

        /// <summary>
        /// Refresh margin's data and redraw it.
        /// </summary>
        private void UpdateMargin()
        {
            bool enabled = false;

            var task = new Task(() =>
            {
                enabled = RefreshVersionControl();
            });

            task.ContinueWith(
                t =>
                {
                    SetMarginEnabled(enabled);
                    Redraw(false);
                },
                TaskScheduler.FromCurrentSynchronizationContext());

            task.Start();
        }

        /// <summary>
        /// Refresh version control status and update associated fields.
        /// </summary>
        /// <returns>
        /// Returns <c>false</c>, if no connection to a team project.
        /// Returns <c>false</c>, if current text document (file) not associated with version control.
        /// Otherwise, returns <c>true</c>.
        /// </returns>
        private bool RefreshVersionControl()
        {
            string tfsServerUriString = _tfExt.ActiveProjectContext.DomainUri;
            if (string.IsNullOrEmpty(tfsServerUriString))
            {
                _versionControl = null;
                _versionControlItem = null;
                return false;
            }

            TfsTeamProjectCollection tfsProjCollections = TfsTeamProjectCollectionFactory.GetTeamProjectCollection(new Uri(tfsServerUriString));
            _versionControl = (VersionControlServer)tfsProjCollections.GetService(typeof(VersionControlServer));
            try
            {
                _versionControlItem = GetVersionControlItem();
            }
            catch (VersionControlItemNotFoundException)
            {
                _versionControlItem = null;
                return false;
            }
            catch (TeamFoundationServiceUnavailableException)
            {
                _versionControlItem = null;
                return false;
            }

            DownloadVersionControlItem();
            return true;
        }

        /// <summary>
        /// Observation over VersionControlItem up-dating at regular intervals.
        /// </summary>
        private void ObserveVersionControlItem()
        {
            while (true)
            {
                try
                {
                    const int versionControlItemObservationInterval = 30000;
                    System.Threading.Thread.Sleep(versionControlItemObservationInterval);

                    if (_versionControlItemWatcherCancelled)
                        break;

                    Item versionControlItem;
                    try
                    {
                        versionControlItem = GetVersionControlItem();
                    }
                    catch (VersionControlItemNotFoundException)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            SetMarginEnabled(false);
                            Redraw(false);
                        });

                        break;
                    }
                    catch (TeamFoundationServiceUnavailableException)
                    {
                        continue;
                    }

                    if (_versionControlItemWatcherCancelled)
                        break;

                    if (_versionControlItem == null || versionControlItem.CheckinDate != _versionControlItem.CheckinDate)
                    {
                        _versionControlItem = versionControlItem;
                        DownloadVersionControlItem();
                        Dispatcher.Invoke(() => Redraw(false));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Unhandled exception in ObserveVersionControlItem: " + ex, MarginName);
                }
            }
        }

        /// <summary>
        /// Get version control item for current text document.
        /// </summary>
        /// <returns>Committed version of a file in the version control server.</returns>
        /// <exception cref="VersionControlItemNotFoundException">The document file is not linked to version control.</exception>
        /// <exception cref="TeamFoundationServiceUnavailableException">Team Foundation Service unavailable.</exception>
        private Item GetVersionControlItem()
        {
            try
            {
                // Be careful, VersionControlServer.GetItem is slow.
                return _versionControl.GetItem(_textDoc.FilePath, VersionSpec.Latest);
            }
            catch (VersionControlException ex)
            {
                string msg = string.Format("Item not found in repository on path \"{0}\".", _textDoc.FilePath);
                throw new VersionControlItemNotFoundException(msg, ex);
            }
        }

        /// <summary>
        /// Download version control item for current text document to stream.
        /// </summary>
        private void DownloadVersionControlItem()
        {
            _versionControlItemStream = new MemoryStream();
            _versionControlItem.DownloadFile().CopyTo(_versionControlItemStream);
        }

        /// <summary>
        /// Update difference between lines asynchronously, if needed, and redraw the margin.
        /// </summary>
        /// <param name="useCache">Use cached differences.</param>
        private void Redraw(bool useCache)
        {
            if (!_isEnabled)
            {
                _cachedChangedLines.Clear();
                Children.Clear();
                return;
            }

            try
            {
                if (!useCache)
                {
                    var task = new Task(() =>
                    {
                        _cachedChangedLines = GetChangedLineNumbers();
                    });

                    // For error handling.
                    task.ContinueWith(
                        t =>
                        {
                            Debug.Assert(t.IsFaulted, "task.IsFaulted should be true.");
                            Debug.Assert(t.Exception != null, "task.Exception is null.");
                            Exception thrownException = t.Exception.InnerException;
                            ShowException(thrownException);
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.FromCurrentSynchronizationContext());

                    // If it succeeded.
                    task.ContinueWith(
                        t =>
                        {
                            Debug.Assert(!t.IsFaulted, "task.IsFaulted should be false.");
                            Redraw(true);
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnRanToCompletion,
                        TaskScheduler.FromCurrentSynchronizationContext());

                    task.Start();
                }
                else
                {
                    DrawMargins();                    
                }
            }
            catch (Exception ex)
            {
                ShowException(ex);
            }
        }

        /// <summary>
        /// Draw margins for each changed line.
        /// </summary>
        private void DrawMargins()
        {
            Debug.Assert(_textView != null, "_textView is null.");
            Debug.Assert(_textView.TextSnapshot != null, "_textView.TextSnapshot is null.");
            Debug.Assert(_cachedChangedLines != null, "_cachedChangedLines is null.");
            Debug.Assert(_outliningManager != null, "_outliningManager is null.");
            Debug.Assert(_marginSettings != null, "_marginSettings is null.");

            Children.Clear();

            var rectMap = new Dictionary<double, KeyValuePair<LineDiffType, Rectangle>>();
            foreach (KeyValuePair<int, LineDiffType> changedLine in _cachedChangedLines)
            {
                ITextSnapshotLine line = _textView.TextSnapshot.GetLineFromLineNumber(changedLine.Key - 1);
                Debug.Assert(line != null, "line is null.");

                IWpfTextViewLine viewLine = _textView.GetTextViewLineContainingBufferPosition(line.Start);
                Debug.Assert(viewLine != null, "viewLine is null.");

                if (rectMap.ContainsKey(viewLine.Top))
                {
                    #if DEBUG
                        IEnumerable<ICollapsed> collapsedRegions = _outliningManager.GetCollapsedRegions(viewLine.Extent, true);
                        bool lineIsCollapsed = collapsedRegions.Any();
                        Debug.Assert(lineIsCollapsed, "line should be collapsed.");
                    #endif

                    KeyValuePair<LineDiffType, Rectangle> rectMapValue = rectMap[viewLine.Top];
                    if (rectMapValue.Key != LineDiffType.Modified && rectMapValue.Key != changedLine.Value)
                    {
                        rectMapValue.Value.Fill = _marginSettings.ModifiedLineMarginBrush;
                        rectMap[viewLine.Top] = new KeyValuePair<LineDiffType, Rectangle>(LineDiffType.Modified, rectMapValue.Value);
                    }

                    continue;
                }

                switch (viewLine.VisibilityState)
                {
                    case VisibilityState.Unattached:
                    case VisibilityState.Hidden:
                        continue;

                    case VisibilityState.PartiallyVisible:
                    case VisibilityState.FullyVisible:
                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }

                var rect = new Rectangle { Height = viewLine.Height, Width = Width };
                SetLeft(rect, 0);
                SetTop(rect, viewLine.Top - _textView.ViewportTop);
                rectMap.Add(viewLine.Top, new KeyValuePair<LineDiffType, Rectangle>(changedLine.Value, rect));

                switch (changedLine.Value)
                {
                    case LineDiffType.Added:
                        rect.Fill = _marginSettings.AddedLineMarginBrush;
                        break;
                    case LineDiffType.Modified:
                        rect.Fill = _marginSettings.ModifiedLineMarginBrush;
                        break;
                    case LineDiffType.Removed:
                        rect.Fill = _marginSettings.RemovedLineMarginBrush;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                Children.Add(rect);
            }
        }

        /// <summary>
        /// Get differences between an original stream and a modified stream.
        /// </summary>
        /// <param name="originalStream">Original stream.</param>
        /// <param name="originalEncoding">Encoding of original stream.</param>
        /// <param name="modifiedStream">Modified stream.</param>
        /// <param name="modifiedEncoding">Encoding of modified stream.</param>
        /// <returns>A summary of the differences between two streams.</returns>
        private DiffSummary GetDifference(Stream originalStream, Encoding originalEncoding, Stream modifiedStream, Encoding modifiedEncoding)
        {
            var diffOptions = new DiffOptions { UseThirdPartyTool = false };
            DiffSummary diffSummary;

            lock (_differenceLockObject)
            {
                originalStream.Position = 0;
                modifiedStream.Position = 0;

                diffSummary = DiffUtil.Diff(
                    originalStream,
                    originalEncoding,
                    modifiedStream,
                    modifiedEncoding,
                    diffOptions,
                    true);
            }

            return diffSummary;
        }

        /// <summary>
        /// Get differences between the document's local file and his source control version.
        /// </summary>
        /// <returns>
        /// Collection that contains the result of comparing the document's local file with his source control version.
        /// <para/>Each element is a pair of key and value: the key is a line number (one-based), the value is a type of difference.
        /// </returns>
        private Dictionary<int, LineDiffType> GetChangedLineNumbers()
        {
            Debug.Assert(_textDoc != null, "_textDoc is null.");
            Debug.Assert(_versionControl != null, "_versionControl is null.");

            byte[] textBytes = _textDoc.Encoding.GetBytes(_textView.TextSnapshot.GetText());
            Stream sourceStream = new MemoryStream(textBytes);

            DiffSummary diffSummary = GetDifference(_versionControlItemStream, Encoding.GetEncoding(_versionControlItem.Encoding), sourceStream, _textDoc.Encoding);
            DiffSegment diffSegment = DiffSegment.Convert(diffSummary.Changes, diffSummary.OriginalLineCount, diffSummary.ModifiedLineCount);

            var dict = new Dictionary<int, LineDiffType>();
            while (diffSegment != null && diffSegment.Next != null)
            {
                int diffLineStart = diffSegment.ModifiedStart + diffSegment.ModifiedLength + 1;
                int diffLineEnd = diffSegment.Next.ModifiedStart;

                if (diffLineEnd >= diffLineStart)
                {
                    for (int i = diffLineStart; i <= diffLineEnd; i++)
                    {
                        int diffLineOrigStart = diffSegment.OriginalStart + diffSegment.OriginalLength + 1;
                        int diffLineOrigEnd = diffSegment.Next.OriginalStart;
                        var diffType = diffLineOrigStart - diffLineOrigEnd > 0
                            ? LineDiffType.Added
                            : LineDiffType.Modified;

                        dict.Add(i, diffType);
                    }
                }
                else
                {
                    Debug.Assert(diffLineEnd - diffLineStart == -1, "The difference between diffLineEnd and diffLineStart is not equal to -1.");

                    // Lines was removed between diffLineEnd and diffLineStart.
                    dict[diffLineStart] = LineDiffType.Removed;
                    if (diffLineEnd != 0)
                        dict[diffLineEnd] = LineDiffType.Removed;
                }

                diffSegment = diffSegment.Next;
            }

            return dict;
        }

        /// <summary>
        /// Show unhandled exception that was thrown by the margin.
        /// </summary>
        /// <param name="ex">The exception instance.</param>
        private void ShowException(Exception ex)
        {
            string msg = string.Format(
                "Unhandled exception was thrown in {0}.{2}" +
                "Please contact with developer. You can copy this message to the Clipboard with CTRL+C.{2}{2}" +
                "{1}",
                MarginName,
                ex,
                Environment.NewLine);

            MessageBox.Show(msg, MarginName, MessageBoxButton.OK, MessageBoxImage.Error);
        }

        /// <summary>
        /// Throw <see cref="ObjectDisposedException"/> if the margin was disposed.
        /// </summary>
        private void ThrowIfDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(MarginName);
        }

        #region Event handlers

        /// <summary>
        /// Event handler that occurs when the <see cref="IWpfTextView.ZoomLevel"/> is set.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event arguments.</param>
        private void OnTextViewZoomLevelChanged(object sender, ZoomLevelChangedEventArgs e)
        {
            Redraw(true);
        }

        /// <summary>
        /// Event handler that occurs when the document has been loaded from or saved to disk. 
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event arguments.</param>
        private void OnTextDocFileActionOccurred(object sender, TextDocumentFileActionEventArgs e)
        {
            Redraw(false);
        }

        /// <summary>
        /// Event handler that occurs when the text editor performs a text line layout.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event arguments.</param>
        private void OnTextViewLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (e.VerticalTranslation)
            {
                Redraw(true);
            }
            else if (e.NewOrReformattedLines.Count > 0 || e.NewOrReformattedSpans.Count > 0 || e.TranslatedLines.Count > 0 || e.TranslatedSpans.Count > 0)
            {
                Redraw(false);
            }
        }

        /// <summary>
        /// Event handler that occurs when <see cref="IEditorFormatMap"/> changes.
        /// </summary>
        /// <param name="sender">Event sender.</param>
        /// <param name="e">Event arguments.</param>
        private void OnFormatMapFormatMappingChanged(object sender, FormatItemsEventArgs e)
        {
            _marginSettings.Refresh();
            Redraw(true);
        }

        private void OnVersionControlCommitCheckin(object sender, CommitCheckinEventArgs e)
        {
            if (_versionControlItem == null)
                return;

            string serverItem = _versionControlItem.ServerItem;
            bool itemCommitted = e.Changes.Any(change => change.ServerItem == serverItem);
            if (itemCommitted)
            {
                var task = new Task(() =>
                {
                    try
                    {
                        _versionControlItem = GetVersionControlItem();
                        DownloadVersionControlItem();
                        Dispatcher.Invoke(() => Redraw(false));
                    }
                    catch (VersionControlItemNotFoundException)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            SetMarginEnabled(false);
                            Redraw(false);
                        });
                    }
                    catch (TeamFoundationServiceUnavailableException)
                    {
                    }
                });

                task.Start();
            }
        }

        #endregion Event handlers
    }
}