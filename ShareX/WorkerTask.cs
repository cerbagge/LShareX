#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Bitmap = SkiaSharp.SKBitmap;
using Image = SkiaSharp.SKBitmap;
using MessageBox = ShareX.AvaloniaUI.MessageBox;
using MessageBoxButtons = ShareX.AvaloniaUI.MessageBoxButtons;
using MessageBoxIcon = ShareX.AvaloniaUI.MessageBoxIcon;

namespace ShareX
{
    public class WorkerTask : IDisposable
    {
        public delegate void TaskEventHandler(WorkerTask task);
        public delegate void TaskImageEventHandler(WorkerTask task, Bitmap image);

        public event TaskEventHandler StatusChanged, TaskCompleted;
        public event TaskImageEventHandler ImageReady;

        public TaskInfo Info { get; private set; }
        public TaskStatus Status { get; private set; }
        public bool IsBusy => Status == TaskStatus.InQueue || IsWorking;
        public bool IsWorking => Status == TaskStatus.Preparing || Status == TaskStatus.Working || Status == TaskStatus.Stopping;
        public bool StopRequested { get; private set; }
        public bool RequestSettingUpdate { get; private set; }
        public bool EarlyURLCopied { get; private set; }
        public Stream Data { get; private set; }
        public Bitmap Image { get; private set; }
        public bool KeepImage { get; set; }
        public string Text { get; private set; }

        private ThreadWorker threadWorker;

        #region Constructors

        private WorkerTask(TaskSettings taskSettings)
        {
            Status = TaskStatus.InQueue;
            Info = new TaskInfo(taskSettings);
        }

        public static WorkerTask CreateHistoryTask(RecentTask recentTask)
        {
            WorkerTask task = new WorkerTask(null);
            task.Status = TaskStatus.History;
            task.Info.FilePath = recentTask.FilePath;
            task.Info.FileName = recentTask.FileName;
            task.Info.Result.URL = recentTask.URL;
            task.Info.Result.ThumbnailURL = recentTask.ThumbnailURL;
            task.Info.Result.DeletionURL = recentTask.DeletionURL;
            task.Info.Result.ShortenedURL = recentTask.ShortenedURL;
            task.Info.TaskEndTime = recentTask.Time;

            return task;
        }

        public static WorkerTask CreateDataUploaderTask(EDataType dataType, Stream stream, string fileName, TaskSettings taskSettings)
        {
            WorkerTask task = new WorkerTask(taskSettings);
            task.Info.Job = TaskJob.DataUpload;
            task.Info.DataType = dataType;
            task.Info.FileName = fileName;
            task.Data = stream;
            return task;
        }

        public static WorkerTask CreateFileUploaderTask(string filePath, TaskSettings taskSettings)
        {
            WorkerTask task = new WorkerTask(taskSettings);
            task.Info.FilePath = filePath;
            task.Info.DataType = TaskHelpers.FindDataType(task.Info.FilePath, taskSettings);

            if (task.Info.TaskSettings.UploadSettings.FileUploadUseNamePattern)
            {
                string ext = FileHelpers.GetFileNameExtension(task.Info.FilePath);
                task.Info.FileName = TaskHelpers.GetFileName(task.Info.TaskSettings, ext);
            }

            if (task.Info.TaskSettings.AdvancedSettings.ProcessImagesDuringFileUpload && task.Info.DataType == EDataType.Image)
            {
                task.Info.Job = TaskJob.Job;
                task.Image = SkiaImageHelpers.LoadImage(task.Info.FilePath);
            }
            else
            {
                task.Info.Job = TaskJob.FileUpload;

                if (!task.LoadFileStream())
                {
                    return null;
                }
            }

            return task;
        }

        public static WorkerTask CreateImageUploaderTask(TaskMetadata metadata, TaskSettings taskSettings, string customFileName = null)
        {
            WorkerTask task = new WorkerTask(taskSettings);
            task.Info.Job = TaskJob.Job;
            task.Info.DataType = EDataType.Image;

            if (!string.IsNullOrEmpty(customFileName))
            {
                task.Info.FileName = FileHelpers.AppendExtension(customFileName, "bmp");
            }
            else
            {
                task.Info.FileName = TaskHelpers.GetFileName(taskSettings, "bmp", metadata);
            }

            task.Info.Metadata = metadata;
            task.Image = metadata.Image;
            return task;
        }

        public static WorkerTask CreateTextUploaderTask(string text, TaskSettings taskSettings)
        {
            WorkerTask task = new WorkerTask(taskSettings);
            task.Info.Job = TaskJob.TextUpload;
            task.Info.DataType = EDataType.Text;
            task.Info.FileName = TaskHelpers.GetFileName(taskSettings, taskSettings.AdvancedSettings.TextFileExtension);
            task.Text = text;
            return task;
        }

        public static WorkerTask CreateFileJobTask(string filePath, TaskMetadata metadata, TaskSettings taskSettings, string customFileName = null)
        {
            WorkerTask task = new WorkerTask(taskSettings);
            task.Info.FilePath = filePath;
            task.Info.DataType = TaskHelpers.FindDataType(task.Info.FilePath, taskSettings);

            if (!string.IsNullOrEmpty(customFileName))
            {
                string ext = FileHelpers.GetFileNameExtension(task.Info.FilePath);
                task.Info.FileName = FileHelpers.AppendExtension(customFileName, ext);
            }
            else if (task.Info.TaskSettings.UploadSettings.FileUploadUseNamePattern)
            {
                string ext = FileHelpers.GetFileNameExtension(task.Info.FilePath);
                task.Info.FileName = TaskHelpers.GetFileName(task.Info.TaskSettings, ext);
            }

            task.Info.Metadata = metadata;
            task.Info.Job = TaskJob.Job;

            if (task.Info.IsUploadJob && !task.LoadFileStream())
            {
                return null;
            }

            return task;
        }

        public static WorkerTask CreateDownloadTask(string url, bool upload, TaskSettings taskSettings)
        {
            WorkerTask task = new WorkerTask(taskSettings);
            task.Info.Job = upload ? TaskJob.DownloadUpload : TaskJob.Download;

            string fileName = URLHelpers.URLDecode(url, 10);
            fileName = URLHelpers.GetFileName(fileName);
            fileName = FileHelpers.SanitizeFileName(fileName);

            if (task.Info.TaskSettings.UploadSettings.FileUploadUseNamePattern)
            {
                string ext = FileHelpers.GetFileNameExtension(fileName);
                fileName = TaskHelpers.GetFileName(task.Info.TaskSettings, ext);
            }

            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            task.Info.FileName = fileName;
            task.Info.DataType = TaskHelpers.FindDataType(task.Info.FileName, taskSettings);
            task.Info.Result.URL = url;
            return task;
        }

        #endregion Constructors

        public void Start()
        {
            if (Status == TaskStatus.InQueue && !StopRequested)
            {
                Info.TaskStartTime = DateTime.Now;

                threadWorker = new ThreadWorker();
                Prepare();
                threadWorker.DoWork += ThreadDoWork;
                threadWorker.Completed += ThreadCompleted;
                threadWorker.Start(ApartmentState.STA);
            }
        }

        private void Prepare()
        {
            Status = TaskStatus.Preparing;

            switch (Info.Job)
            {
                case TaskJob.Job:
                case TaskJob.TextUpload:
                    Info.Status = Strings.UploadTask_Prepare_Preparing;
                    break;
                default:
                    Info.Status = Strings.UploadTask_Prepare_Starting;
                    break;
            }

            OnStatusChanged();
        }

        public void Stop()
        {
            StopRequested = true;

            switch (Status)
            {
                case TaskStatus.InQueue:
                    OnTaskCompleted();
                    break;
                case TaskStatus.Preparing:
                case TaskStatus.Working:
                    Status = TaskStatus.Stopping;
                    Info.Status = Strings.UploadTask_Stop_Stopping;
                    OnStatusChanged();
                    break;
            }
        }

        public void ShowErrorWindow()
        {
            if (Info != null && Info.Result != null && Info.Result.IsError)
            {
                string errors = Info.Result.ErrorsToString();

                if (!string.IsNullOrEmpty(errors))
                {
                    ErrorWindowIntegration.Show(Strings.UploadInfoManager_ShowErrors_Upload_errors, errors,
                        AppPaths.LogsFilePath, Links.GitHubIssues, false);
                }
            }
        }

        private void ThreadDoWork()
        {
            try
            {
                StopRequested = !DoThreadJob();

                OnImageReady();

                // LShareX: uploading was removed, tasks never produce a URL.
                Info.Result.IsURLExpected = false;
            }
            finally
            {
                KeepImage = Image != null && Info.TaskSettings.GeneralSettings.ShowToastNotificationAfterTaskCompleted;

                Dispose();

                if (EarlyURLCopied && (StopRequested || Info.Result == null || string.IsNullOrEmpty(Info.Result.URL)) && ClipboardHelpers.ContainsText())
                {
                    ClipboardHelpers.Clear();
                }

                if ((Info.Job == TaskJob.Job || (Info.Job == TaskJob.FileUpload && Info.TaskSettings.AdvancedSettings.UseAfterCaptureTasksDuringFileUpload))
                    && Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.DeleteFile) && !string.IsNullOrEmpty(Info.FilePath) && File.Exists(Info.FilePath))
                {
                    File.Delete(Info.FilePath);
                }
            }
        }

        private void AddErrorMessage(string error)
        {
            if (Info.Result == null)
            {
                Info.Result = new UploadResult();
            }

            Info.Result.Errors.Add(error);
        }

        private bool DoThreadJob()
        {
            if (Info.IsUploadJob && Info.TaskSettings.AdvancedSettings.AutoClearClipboard)
            {
                ClipboardHelpers.Clear();
            }

            if (Info.Job == TaskJob.Download || Info.Job == TaskJob.DownloadUpload)
            {
                bool downloadResult = DownloadFromURL(Info.Job == TaskJob.DownloadUpload);

                if (!downloadResult)
                {
                    return false;
                }
                else if (Info.Job == TaskJob.Download)
                {
                    return true;
                }
            }

            if (Info.Job == TaskJob.Job)
            {
                if (!DoAfterCaptureJobs())
                {
                    return false;
                }

                if (!DoFileJobs())
                {
                    return false;
                }
            }
            else if (Info.Job == TaskJob.TextUpload && !string.IsNullOrEmpty(Text))
            {
                DoTextJobs();
            }
            else if (Info.Job == TaskJob.FileUpload && Info.TaskSettings.AdvancedSettings.UseAfterCaptureTasksDuringFileUpload)
            {
                if (!DoFileJobs())
                {
                    return false;
                }
            }

            if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.DoOCR))
            {
                DoOCR();
            }

            if (Info.IsUploadJob && Data != null && Data.CanSeek)
            {
                Data.Position = 0;
            }

            return true;
        }

        private bool DoAfterCaptureJobs()
        {
            if (Image == null)
            {
                return true;
            }

            if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.BeautifyImage))
            {
                Image = TaskHelpers.BeautifyImage(Image, Info.TaskSettings);

                if (Image == null)
                {
                    return false;
                }
            }

            if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.AnnotateImage))
            {
                // The editor blocks the task until it closes, so copy the capture now and let the user paste it without having to edit it.
                if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.CopyImageToClipboard))
                {
                    ClipboardHelpers.CopyImage(Image, Info.FileName);
                    DebugHelper.WriteLine("Image copied to clipboard before opening the image editor.");
                }

                Image = TaskHelpers.AnnotateImage(Image, null, Info.TaskSettings, true);

                if (Image == null)
                {
                    return false;
                }
            }

            if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.CopyImageToClipboard))
            {
                ClipboardHelpers.CopyImage(Image, Info.FileName);
                DebugHelper.WriteLine("Image copied to clipboard.");
            }

            if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.PinToScreen))
            {
                Image imageCopy = Image?.Copy();
                TaskHelpers.PinToScreen(imageCopy, Info.TaskSettings);
            }

            if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.SendImageToPrinter))
            {
                TaskHelpers.PrintImage(Image);
            }

            Info.Metadata.Image = Image;

            if (Info.TaskSettings.AfterCaptureJob.HasFlagAny(AfterCaptureTasks.SaveImageToFile, AfterCaptureTasks.SaveImageToFileWithDialog, AfterCaptureTasks.DoOCR,
                AfterCaptureTasks.UploadImageToHost, AfterCaptureTasks.AnalyzeImage))
            {
                ImageData imageData = TaskHelpers.PrepareImage(Image, Info.TaskSettings);
                Data = imageData.ImageStream;
                Info.FileName = Path.ChangeExtension(Info.FileName, imageData.ImageFormat.GetFileExtension());

                if (Info.TaskSettings.AfterCaptureJob.HasFlagAny(AfterCaptureTasks.SaveImageToFile, AfterCaptureTasks.AnalyzeImage))
                {
                    string screenshotsFolder = TaskHelpers.GetScreenshotsFolder(Info.TaskSettings, Info.Metadata);
                    string filePath = TaskHelpers.HandleExistsFile(screenshotsFolder, Info.FileName, Info.TaskSettings);

                    if (!string.IsNullOrEmpty(filePath))
                    {
                        Info.FilePath = filePath;
                        imageData.Write(Info.FilePath);
                        DebugHelper.WriteLine("Image saved to file: " + Info.FilePath);
                    }
                }

                if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.SaveImageToFileWithDialog))
                {
                    string initialDirectory = Directory.Exists(HelpersOptions.LastSaveDirectory)
                        ? HelpersOptions.LastSaveDirectory : TaskHelpers.GetScreenshotsFolder(Info.TaskSettings, Info.Metadata);
                    bool imageSaved;
                    do
                    {
                        string selectedPath = FileDialogHelpers.SaveFile(
                            Strings.UploadTask_DoAfterCaptureJobs_Choose_a_folder_to_save + " " + Path.GetFileName(Info.FileName),
                            string.Format("*{0}|*{0}|{1}|*.*", Path.GetExtension(Info.FileName), Strings.WorkerTask_AllFilesFilter),
                            Info.FileName, initialDirectory, Path.GetExtension(Info.FileName).TrimStart('.'));
                        if (string.IsNullOrEmpty(selectedPath)) break;
                        Info.FilePath = selectedPath;
                        HelpersOptions.LastSaveDirectory = Path.GetDirectoryName(Info.FilePath);
                        imageSaved = imageData.Write(Info.FilePath);
                        if (imageSaved) DebugHelper.WriteLine("Image saved to file with dialog: " + Info.FilePath);
                    } while (!imageSaved);
                }

                if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.SaveThumbnailImageToFile))
                {
                    string thumbnailFileName, thumbnailFolder;

                    if (!string.IsNullOrEmpty(Info.FilePath))
                    {
                        thumbnailFileName = Path.GetFileName(Info.FilePath);
                        thumbnailFolder = Path.GetDirectoryName(Info.FilePath);
                    }
                    else
                    {
                        thumbnailFileName = Info.FileName;
                        thumbnailFolder = TaskHelpers.GetScreenshotsFolder(Info.TaskSettings, Info.Metadata);
                    }

                    Info.ThumbnailFilePath = TaskHelpers.CreateThumbnail(Image, thumbnailFolder, thumbnailFileName, Info.TaskSettings);

                    if (!string.IsNullOrEmpty(Info.ThumbnailFilePath))
                    {
                        DebugHelper.WriteLine("Thumbnail saved to file: " + Info.ThumbnailFilePath);
                    }
                }
            }

            return true;
        }

        private bool DoFileJobs()
        {
            if (!string.IsNullOrEmpty(Info.FilePath) && File.Exists(Info.FilePath))
            {
                if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.PerformActions) && Info.TaskSettings.ExternalPrograms != null)
                {
                    IEnumerable<ExternalProgram> actions = Info.TaskSettings.ExternalPrograms.Where(x => x.IsActive);

                    if (actions.Count() > 0)
                    {
                        bool isFileModified = false;
                        string fileName = Info.FileName;

                        foreach (ExternalProgram fileAction in actions)
                        {
                            string modifiedPath = fileAction.Run(Info.FilePath);

                            string resultPath = string.IsNullOrEmpty(modifiedPath) ? Info.FilePath : modifiedPath;

                            if (!File.Exists(resultPath))
                            {
                                DebugHelper.WriteLine($"Action result file does not exist: \"{resultPath}\"");
                                return false;
                            }

                            if (!string.IsNullOrEmpty(modifiedPath))
                            {
                                isFileModified = true;
                                Info.FilePath = modifiedPath;

                                if (Data != null)
                                {
                                    Data.Dispose();
                                }

                                fileAction.DeletePendingInputFile();
                            }
                        }

                        if (isFileModified)
                        {
                            string extension = FileHelpers.GetFileNameExtension(Info.FilePath);
                            Info.FileName = FileHelpers.ChangeFileNameExtension(fileName, extension);

                            if (!LoadFileStream())
                            {
                                return false;
                            }
                        }
                    }
                }

                if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.CopyFileToClipboard))
                {
                    // LShareX: when the image is also copied, put the image and the saved file on the clipboard together,
                    // so apps that can't paste images attach the file instead.
                    if (Image != null && Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.CopyImageToClipboard))
                    {
                        ClipboardHelpers.CopyImage(Image, Info.FileName, Info.FilePath);
                    }
                    else
                    {
                        ClipboardHelpers.CopyFile(Info.FilePath);
                    }
                }
                else if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.CopyFilePathToClipboard))
                {
                    ClipboardHelpers.CopyText(Info.FilePath);
                }
                else if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.CopyFolderPathToClipboard))
                {
                    ClipboardHelpers.CopyText(Path.GetDirectoryName(Info.FilePath));
                }

                if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.ShowInExplorer))
                {
                    FileHelpers.OpenFolderWithFile(Info.FilePath);
                }

                if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.AnalyzeImage) && Info.DataType == EDataType.Image)
                {
                    TaskHelpers.AnalyzeImage(Info.FilePath, Info.TaskSettings);
                }

                if (Info.TaskSettings.AfterCaptureJob.HasFlag(AfterCaptureTasks.ScanQRCode) && Info.DataType == EDataType.Image)
                {
                    TaskHelpers.OpenQRCodeScanFromImageFile(Info.FilePath);
                }
            }

            return true;
        }

        private void DoTextJobs()
        {
            if (Info.TaskSettings.AdvancedSettings.TextTaskSaveAsFile)
            {
                string screenshotsFolder = TaskHelpers.GetScreenshotsFolder(Info.TaskSettings);
                string filePath = TaskHelpers.HandleExistsFile(screenshotsFolder, Info.FileName, Info.TaskSettings);

                if (!string.IsNullOrEmpty(filePath))
                {
                    Info.FilePath = filePath;
                    FileHelpers.CreateDirectoryFromFilePath(Info.FilePath);
                    File.WriteAllText(Info.FilePath, Text, Encoding.UTF8);
                    DebugHelper.WriteLine("Text saved to file: " + Info.FilePath);
                }
            }

            byte[] byteArray = Encoding.UTF8.GetBytes(Text);
            Data = new MemoryStream(byteArray);
        }

        private bool DownloadFromURL(bool upload)
        {
            string url = Info.Result.URL.Trim();
            Info.Result.URL = "";

            if (!Info.TaskSettings.UploadSettings.FileUploadUseNamePattern)
            {
                try
                {
                    string fileName = WebHelpers.GetFileNameFromWebServerAsync(url).GetAwaiter().GetResult();

                    if (!string.IsNullOrEmpty(fileName))
                    {
                        Info.FileName = FileHelpers.SanitizeFileName(fileName);
                    }
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                }
            }

            string screenshotsFolder = TaskHelpers.GetScreenshotsFolder(Info.TaskSettings);
            Info.FilePath = TaskHelpers.HandleExistsFile(screenshotsFolder, Info.FileName, Info.TaskSettings);

            if (!string.IsNullOrEmpty(Info.FilePath))
            {
                Info.Status = Strings.UploadTask_DownloadAndUpload_Downloading;
                OnStatusChanged();

                try
                {
                    WebHelpers.DownloadFileAsync(url, Info.FilePath).GetAwaiter().GetResult();

                    if (upload)
                    {
                        LoadFileStream();
                    }

                    return true;
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e);
                    MessageBox.Show(string.Format(Strings.UploadManager_DownloadAndUploadFile_Download_failed, e), "ShareX", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }

            return false;
        }

        private void DoOCR()
        {
            if (Image != null && Info.DataType == EDataType.Image)
            {
                TaskHelpers.OCRImage(Image, Info.TaskSettings).GetAwaiter().GetResult();
            }
        }

        private bool LoadFileStream()
        {
            try
            {
                Data = new FileStream(Info.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception e)
            {
                e.ShowError();
                return false;
            }

            return true;
        }

        private void ThreadCompleted()
        {
            OnTaskCompleted();
        }

        private void OnStatusChanged()
        {
            if (StatusChanged != null)
            {
                threadWorker.InvokeAsync(() => StatusChanged(this));
            }
        }

        private void OnImageReady()
        {
            if (ImageReady != null)
            {
                Bitmap image = null;

                if (Image != null)
                {
                    image = (Bitmap)Image.Copy();
                }

                threadWorker.InvokeAsync(() =>
                {
                    using (image)
                    {
                        ImageReady(this, image);
                    }
                });
            }
        }

        private void OnTaskCompleted()
        {
            Info.TaskEndTime = DateTime.Now;

            if (StopRequested)
            {
                Status = TaskStatus.Stopped;
                Info.Status = Strings.UploadTask_OnUploadCompleted_Stopped;
            }
            else if (Info.Result.IsError)
            {
                Status = TaskStatus.Failed;
                Info.Status = Strings.TaskManager_task_UploadCompleted_Error;
            }
            else
            {
                Status = TaskStatus.Completed;
                Info.Status = Strings.UploadTask_OnUploadCompleted_Done;
            }

            TaskCompleted?.Invoke(this);

            Dispose();
        }

        public void Dispose()
        {
            if (Data != null)
            {
                Data.Dispose();
                Data = null;
            }

            if (!KeepImage && Image != null)
            {
                Image.Dispose();
                Image = null;
            }
        }
    }
}
