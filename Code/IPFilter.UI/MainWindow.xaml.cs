﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows;
using Ionic.Zip;
using IPFilter.UI.Properties;

namespace IPFilter.UI
{
    /// <summary>
    /// Interaction logic for the main window.
    /// </summary>
    public partial class MainWindow
    {
        string[] zipContentTypes = new string[]{};

        string[] gzipContentTypes = new string[]{};

        readonly BackgroundWorker worker;
        IEnumerable<FileMirror> mirrors;
        UpdateState state;

        public MainWindow()
        {
            worker = new BackgroundWorker
                         {
                             WorkerReportsProgress = true,
                             WorkerSupportsCancellation = true
                         };

            worker.DoWork += DoWork;
            worker.ProgressChanged += ProgressChanged;
            worker.RunWorkerCompleted += RunWorkerCompleted;

            InitializeComponent();

            var version = GetAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
            var product = GetAttribute<AssemblyProductAttribute>().Product;

            Title = string.Concat(product, @" ", version);
        }

        static T GetAttribute<T>() where T : Attribute
        {
            return Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(T), true).Cast<T>().Single();
        }

        void RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            //lblStatus.Content = e.Cancelled ? "Cancelled" : "Done";
            if (e.Cancelled) lblStatus.Content = "Cancelled";
            pbProgress.Value = 100;
            SetState( e.Cancelled ? UpdateState.Cancelled : UpdateState.Done);
        }

        void SetState(UpdateState updateState)
        {
            state = updateState;
            RefreshState();
        }

        void RefreshState()
        {
            switch (state)
            {
                case UpdateState.Cancelled:
                case UpdateState.Ready:
                    cboMirror.IsEnabled = cboMirrorProvider.IsEnabled = true;
                    pbProgress.Value = 0;
                    lblStatus.Content = "Ready";
                    btnGo.Content = "Go";
                    btnGo.IsEnabled = true;
                    break;
                case UpdateState.Downloading:
                    cboMirror.IsEnabled = cboMirrorProvider.IsEnabled = false;
                    btnGo.Content = "Cancel";
                    break;
                case UpdateState.Cancelling:
                    if (worker.CancellationPending) break;
                    btnGo.Content = "Cancelling...";
                    btnGo.IsEnabled = false;
                    CancelDownload();
                    break;
                case UpdateState.Done:
                    btnGo.Content = "Done";
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        void CancelDownload()
        {
            worker.CancelAsync();
        }

        void StartDownload()
        {
            var mirrorProvider = (IMirrorProvider)cboMirrorProvider.SelectedItem;
            var mirror = ((FileMirror) cboMirror.SelectedItem);
            string urlString = mirrorProvider.GetUrlForMirror(mirror);
            worker.RunWorkerAsync(urlString);
        }


        void ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            lblStatus.Content = e.UserState.ToString();
            pbProgress.Value = e.ProgressPercentage;
        }

        void ZipProgress(object sender, ZipProgressEventArgs args)
        {
            if (args.EventType != ZipProgressEventType.Extracting_EntryBytesWritten) return;
            var percentage = args.BytesTransferred/args.TotalBytesToTransfer * 100;
            worker.ReportProgress( Convert.ToInt32(percentage), "Extracting..." );
        }

        void DoWork(object sender, DoWorkEventArgs e)
        {
            WebRequest request = WebRequest.Create((string) e.Argument);

            worker.ReportProgress(0, string.Format(CultureInfo.CurrentUICulture, "Contacting {0}", request.RequestUri));
            WebResponse response = request.GetResponse();

            const int bufferSize = 1024*64; // Go in 64K chunks

            var buffer = new byte[bufferSize];

            using (var contentStream = new MemoryStream())
            using (Stream stream = response.GetResponseStream())
            {
                worker.ReportProgress(0, "Downloading...");

                var lastModified = response.Headers[HttpResponseHeader.LastModified];

                if( !string.IsNullOrEmpty(lastModified))
                {
                    lastModified = DateTime.Parse(lastModified).ToString(CultureInfo.CurrentCulture);// "Cannot determine date of file.";
                }
                else
                {
                    lastModified = "Cannot determine date of file.";
                }

                int length = Convert.ToInt32(response.ContentLength);
                double lengthMegs = (double) length/1024/1024;

                int bytesRead = stream.Read(buffer, 0, bufferSize);
                int totalRead = 0;

                CompressionFormat fileFormat = CompressionFormat.None;

                switch (response.ContentType)
                {
                    case "application/gzip":
                    case "application/x-gzip":
                    case "application/x-gunzip":
                    case "application/gzipped":
                    case "application/gzip-compressed":
                    case "gzip/document":
                        fileFormat = CompressionFormat.GZip;
                        break;

                    case "application/zip":
                    case "application/x-zip":
                    case "application/x-zip-compressed":
                    case "multipart/x-zip":
                        fileFormat = CompressionFormat.Zip;
                        break;

                    case "application/x-compressed":
                    case "application/octet-stream":
                    case "text/plain":
                    default:
                        {
                            // Look for the GZip header bytes
                            if (buffer[0] == 31 && buffer[1] == 139)
                            {
                                fileFormat = CompressionFormat.GZip;
                            }
                            else
                            {
                                // Look for the ZIP header bytes.
                                var zipHeaderNumber = BitConverter.ToInt32(buffer, 0);
                                if (zipHeaderNumber == 0x4034b50)
                                {
                                    fileFormat = CompressionFormat.Zip;
                                }
                            }
                        }
                        break;
                }

                while (bytesRead > 0)
                {
                    contentStream.Write(buffer, 0, bytesRead);
                    totalRead += bytesRead;

                    double downloadedMegs = (double) totalRead/1024/1024;
                    double percentage = (double) totalRead/length;

                    worker.ReportProgress(Convert.ToInt32(percentage*100),
                                          string.Format(CultureInfo.CurrentUICulture,
                                                        "Downloaded {0:F2} MB of {1:F2} MB", downloadedMegs, lengthMegs));
                    bytesRead = stream.Read(buffer, 0, bufferSize);

                    if (!worker.CancellationPending) continue;

                    // Cancel
                    e.Cancel = true;
                    return;
                }

                string filterPath = Environment.ExpandEnvironmentVariables(@"%APPDATA%\uTorrent\ipfilter.dat");

                worker.ReportProgress(0, "Decompressing...");

                using (var decompressedStream = new MemoryStream())
                {
                    MessageBoxResult tryToWrite = MessageBoxResult.Yes;

                    try
                    {
                        contentStream.Seek(0, SeekOrigin.Begin);

                        switch(fileFormat)
                        {
                            case CompressionFormat.GZip:

                                // Can't report progress for GZip
                                Dispatcher.Invoke(new Action(() => pbProgress.IsIndeterminate = true));

                                using(var gzipFile = new GZipStream(contentStream, CompressionMode.Decompress))
                                {
                                    var zipByteBuffer = new byte[1024 * 64];
                                    int zipBytesRead;
                                    while ((zipBytesRead = gzipFile.Read(zipByteBuffer, 0, zipByteBuffer.Length)) > 0)
                                    {
                                        decompressedStream.Write(zipByteBuffer, 0, zipBytesRead);
                                    }
                                }
                                break;

                            case CompressionFormat.Zip:
                                using (var zipFile = ZipFile.Read(contentStream, ZipProgress))
                                {
                                    if (zipFile.Entries.Count == 0) throw new ZipException("There are no entries in the zip file.");
                                    if (zipFile.Entries.Count > 1) throw new ZipException("There is more than one file in the zip file. This application will need to be updated to support this.");

                                    var entry = zipFile.Entries.First();

                                    entry.Extract(decompressedStream);
                                }
                                break;

                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                    }
                    catch (Exception ex)
                    {
                        ShowMessageBox("Error Decompressing", // Title / Caption
                                       MessageBoxButton.OK, // Buttons
                                       MessageBoxImage.Error, // Icon
                                       MessageBoxResult.OK, // Default button
                                       "There was a problem decompressing: {0}",
                                       ex.Message
                            ); // RTL culture?

                        return;
                    }

                    if (!File.Exists(filterPath))
                    {
                        var filterDirectory = new DirectoryInfo(Path.GetDirectoryName(filterPath));
                        if (!filterDirectory.Exists) filterDirectory.Create();
                        File.Create(filterPath).Dispose();
                    }

                    worker.ReportProgress(100, "Writing to " + filterPath);

                    while (tryToWrite == MessageBoxResult.Yes)
                    {
                        try
                        {
                            using (FileStream file = File.Open(filterPath, FileMode.Truncate, FileAccess.Write, FileShare.None))
                            {
                                decompressedStream.WriteTo(file);
                                file.Flush();
                                file.Close();
                                tryToWrite = MessageBoxResult.No;
                            }
                        }
                        catch (Exception ex)
                        {
                            tryToWrite = ShowMessageBox("Error Writing ipfilter.dat", // Title / Caption
                                                        MessageBoxButton.YesNo, // Buttons
                                                        MessageBoxImage.Error, // Icon
                                                        MessageBoxResult.Yes, // Default button
                                                        "There was a problem writing to {0}:\n\n{1}",
                                                        filterPath, ex.Message
                                ); // RTL culture?
                        }
                    }

                    SetStatus("IP Filter downloaded. Date of this filter is " + lastModified, false);
                    Dispatcher.Invoke(new Action(() => pbProgress.IsIndeterminate = false));
                }
            }
        }

        MessageBoxResult ShowMessageBox(string title, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultButton, string message, params object[] args)
        {
            string formattedMessage = string.Format(CultureInfo.CurrentCulture, message, args);

            return (MessageBoxResult)Dispatcher.Invoke(new Func<MessageBoxResult>(
                () => MessageBox.Show(this, // Owner
                            formattedMessage, // Message
                            title, // Title / Caption
                            buttons, // Buttons
                            image, // Icon
                            defaultButton, // Default button
                            CultureInfo.CurrentCulture.TextInfo.IsRightToLeft ? MessageBoxOptions.RightAlign | MessageBoxOptions.RtlReading : 0) // RTL culture?
            ));
        }

        void Window_Loaded(object sender, RoutedEventArgs e)
        {
            cboMirrorProvider.Items.Clear();
            cboMirrorProvider.IsEnabled = false;

            cboMirrorProvider.Items.Add(new BlocklistMirrorProvider());
            cboMirrorProvider.Items.Add(new SourceForgeMirrorProvider());

            cboMirrorProvider.SelectedIndex = 0;

            cboMirrorProvider.IsEnabled = true;

            ThreadPool.QueueUserWorkItem(LoadMirrors);

//            if (MessageBox.Show(this, "IP Filter Updater has moved host from Microsoft's Codeplex to GitHub. \r\n\r\n"
//                + "Sorry for the inconvenience, but you will need to download and install the new version manually (thanks for nothing, Codeplex). \r\n\r\n"
//                + "Would you like me to take you to the web page now?", "New version available", MessageBoxButton.YesNo, MessageBoxImage.Exclamation, MessageBoxResult.Yes) == MessageBoxResult.Yes)
//            {
//                Process.Start("http://davidmoore.github.io/ipfilter/");
//            }

            return;

            // TODO: Check if IP Filter is enabled in µTorrent
            string settingsPath = Environment.ExpandEnvironmentVariables(@"%APPDATA%\uTorrent\settings.dat");
            var settings = File.ReadAllText(settingsPath);
            if (settings.Contains("15:ipfilter.enablei0e"))
            {
                MessageBox.Show("You haven't enabled IP Filtering in µTorrent! Go to http://ipfilter.codeplex.com/ for help.", "IP filtering not enabled", MessageBoxButton.OK);
            }

        }

        void LoadMirrors(object currentState)
        {
            SetStatus("Loading mirrors...", true);

            IMirrorProvider selectedMirrorProvider = null;

            Dispatcher.Invoke(
                new Action(
                    () =>
                    {
                        selectedMirrorProvider = (IMirrorProvider)cboMirrorProvider.SelectedItem;
                        //cboMirror.Items.Clear();
                        cboMirror.IsEnabled = false;
                    }));

            var backgroundWorker = new BackgroundWorker();

            backgroundWorker.DoWork += delegate(object sender, DoWorkEventArgs args)
                {
                    mirrors = selectedMirrorProvider.GetMirrors();
                };

            backgroundWorker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs args)
                {
                    Dispatcher.Invoke(new Action(() =>
                    {
                        cboMirror.ItemsSource = mirrors;
                        cboMirror.SelectedIndex = mirrors.Count() - 1;
                        cboMirror.IsEnabled = true;
                        SetStatus("Ready", false);
                        btnGo.IsEnabled = true;
                        pbProgress.IsIndeterminate = false;
                    }));
                };

            backgroundWorker.RunWorkerAsync();
        }

        void SetStatus(string message, bool busy)
        {
            if( !Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(new Action<string, bool>(SetStatus), message, busy);
                return;
            }
            btnGo.IsEnabled = !busy;
            pbProgress.IsIndeterminate = busy;
            lblStatus.Content = message;
        }

        void BtnGoClick(object sender, RoutedEventArgs e)
        {
            switch (state)
            {
                case UpdateState.Cancelled:
                case UpdateState.Ready:
                    state = UpdateState.Downloading;
                    StartDownload();
                    break;
                case UpdateState.Downloading:
                    state = UpdateState.Cancelling;
                    break;
                case UpdateState.Done:
                    Close();
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            cboMirror.IsEnabled = cboMirrorProvider.IsEnabled = false;
            RefreshState();
        }

        private void cboMirrorProvider_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            LoadMirrors(null);
        }
    }

    enum CompressionFormat
    {
        None = 0,
        GZip = 1,
        Zip = 2
    }
}