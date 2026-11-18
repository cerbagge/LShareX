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
using ShareX.ScreenCaptureLib;
using ShareX.ScreenCaptureLib.Presentation.RegionCapture;
using ShareX.Localization;
using ShareX.Tools;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bitmap = SkiaSharp.SKBitmap;

namespace ShareX
{
    public class CaptureRegion : CaptureBase
    {
        // LShareX: only the "Capture multiple regions" hotkey task allows selecting several regions.
        public bool MultiRegion { get; set; }

        // LShareX: kept for the app lifetime so the model is only loaded once.
        private static readonly BackgroundRemovalService backgroundRemovalService = new BackgroundRemovalService();

        protected override TaskMetadata Execute(TaskSettings taskSettings)
        {
            return null;
        }

        protected override async Task<TaskMetadata> ExecuteAsync(TaskSettings taskSettings)
        {
            return await ExecuteRegionCaptureAvaloniaAsync(taskSettings);
        }

        protected async Task<TaskMetadata> ExecuteRegionCaptureAvaloniaAsync(TaskSettings taskSettings)
        {
            Screenshot screenshot = TaskHelpers.GetScreenshot(taskSettings);
            screenshot.CaptureCursor = false;

            bool activeMonitorMode = taskSettings.CaptureSettings.RegionCaptureOptions.ActiveMonitorMode;
            Rectangle screenBounds = activeMonitorMode
                ? CaptureHelpers.GetActiveScreenBounds()
                : CaptureHelpers.GetScreenBounds();

            SKBitmap frozenScreenshot;
            using (Bitmap canvas = activeMonitorMode
                ? screenshot.CaptureActiveMonitor()
                : screenshot.CaptureFullscreen())
            {
                frozenScreenshot = canvas.Copy();
            }

            SKBitmap cursorBitmap = null;
            Point cursorPosition = Point.Empty;

            if (taskSettings.CaptureSettings.ShowCursor)
            {
                CursorData cursorData = new CursorData();
                if (cursorData.IsVisible)
                {
                    using Bitmap cursor = cursorData.ToBitmap(frozenScreenshot, screenBounds.Location);
                    cursorBitmap = cursor.Copy();
                    cursorPosition = new Point(
                        cursorData.DrawPosition.X - screenBounds.X,
                        cursorData.DrawPosition.Y - screenBounds.Y);
                }
            }

            AvaloniaRegionCaptureRequest request = new AvaloniaRegionCaptureRequest
            {
                Screenshot = frozenScreenshot,
                ScreenBounds = screenBounds,
                RegionCaptureOptions = taskSettings.CaptureSettingsReference.RegionCaptureOptions,
                ImageEditorOptions = taskSettings.ToolsSettingsReference.ImageEditorOptions,
                EnableAnnotations = !taskSettings.AdvancedSettings.RegionCaptureDisableAnnotation,
                AllowMultipleRegions = MultiRegion,
                CursorBitmap = cursorBitmap,
                CursorPosition = cursorPosition
            };

            AvaloniaRegionCaptureResult result = await RegionCaptureIntegration.CaptureAsync(request);
            if (result == null)
            {
                return null;
            }

            if (result.RemoveBackground)
            {
                result = await RemoveBackgroundAsync(result, taskSettings);
            }

            if (result.AdditionalImages != null)
            {
                // LShareX: multi region mode saves each region as its own image.
                foreach (SKBitmap regionImage in result.AdditionalImages)
                {
                    using (regionImage)
                    {
                        TaskMetadata regionMetadata = new TaskMetadata(regionImage.Copy());
                        if (result.WindowInfo != null)
                        {
                            regionMetadata.UpdateInfo(result.WindowInfo);
                        }
                        AdditionalCaptures.Add(regionMetadata);
                    }
                }
            }

            using (result.Image)
            {
                Bitmap output = result.Image.Copy();
                TaskMetadata metadata = new TaskMetadata(output);

                if (result.ImageModified)
                {
                    AllowAnnotation = false;
                }

                if (result.WindowInfo != null)
                {
                    metadata.UpdateInfo(result.WindowInfo);
                }

                return metadata;
            }
        }

        // LShareX: "Remove background" toolbar button. If no model is available or removal fails, the original image is kept.
        private static async Task<AvaloniaRegionCaptureResult> RemoveBackgroundAsync(AvaloniaRegionCaptureResult result, TaskSettings taskSettings)
        {
            BackgroundRemoverOptions options = taskSettings.ToolsSettingsReference.BackgroundRemoverOptions;
            BackgroundRemovalModel model = FindBackgroundRemovalModel(options.SelectedModelFileName);

            if (model == null)
            {
                TaskHelpers.ShowNotificationTip(Strings.CaptureRegion_BackgroundRemovalModelMissing, "LShareX", 5000);
                return result;
            }

            try
            {
                SKBitmap image = await Task.Run(() => backgroundRemovalService.RemoveBackground(result.Image, model, options.SelectedDevice).Image);
                List<SKBitmap> additionalImages = null;

                if (result.AdditionalImages != null)
                {
                    additionalImages = [];
                    foreach (SKBitmap regionImage in result.AdditionalImages)
                    {
                        additionalImages.Add(await Task.Run(() => backgroundRemovalService.RemoveBackground(regionImage, model, options.SelectedDevice).Image));
                        regionImage.Dispose();
                    }
                }

                result.Image.Dispose();
                return result with { Image = image, AdditionalImages = additionalImages };
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "Background removal failed.");
                TaskHelpers.ShowNotificationTip(Strings.CaptureRegion_BackgroundRemovalFailed, "LShareX", 5000);
                return result;
            }
        }

        private static BackgroundRemovalModel FindBackgroundRemovalModel(string selectedModelFileName)
        {
            if (!Directory.Exists(AppPaths.ModelsFolder))
            {
                return null;
            }

            string[] modelPaths = Directory.GetFiles(AppPaths.ModelsFolder, "*.onnx").OrderBy(Path.GetFileName).ToArray();
            string modelPath = modelPaths.FirstOrDefault(path => Path.GetFileName(path).Equals(selectedModelFileName, StringComparison.OrdinalIgnoreCase)) ??
                modelPaths.FirstOrDefault();

            if (modelPath == null)
            {
                return null;
            }

            return new BackgroundRemovalModel
            {
                FilePath = modelPath,
                FileName = Path.GetFileName(modelPath),
                FileSize = new FileInfo(modelPath).Length
            };
        }
    }
}
