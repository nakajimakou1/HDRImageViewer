﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

using DXRenderer;
using Windows.UI.Input;
using Windows.Graphics.Display;
using Windows.UI.Core;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;
using Windows.System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace HDRImageViewerCS
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class DXViewerPage : Page
    {

        // Resources used to draw the DirectX content in the XAML page.
        HDRImageViewerRenderer renderer;
        GestureRecognizer gestureRecognizer;
        bool isWindowVisible;

        // Cached information for UI.
        ImageInfo imageInfo;
        ImageCLL imageCLL;
        AdvancedColorInfo dispInfo;
        bool isImageValid;
        RenderOptionsViewModel viewModel;
        public RenderOptionsViewModel ViewModel { get { return viewModel; } }

        public DXViewerPage()
        {
            this.InitializeComponent();

            isWindowVisible = true;
            isImageValid = false;
            imageCLL.maxNits = imageCLL.medNits = -1.0f;

            // Register event handlers for page lifecycle.
            var window = Window.Current.CoreWindow;

            window.KeyUp += OnKeyUp;
            window.VisibilityChanged += OnVisibilityChanged;
            window.ResizeCompleted += OnResizeCompleted;

            var currDispInfo = DisplayInformation.GetForCurrentView();

            currDispInfo.DpiChanged += OnDpiChanged;
            currDispInfo.OrientationChanged += OnOrientationChanged;
            DisplayInformation.DisplayContentsInvalidated += OnDisplayContentsInvalidated;

            currDispInfo.AdvancedColorInfoChanged += OnAdvancedColorInfoChanged;
            var acInfo = currDispInfo.GetAdvancedColorInfo();

            swapChainPanel.CompositionScaleChanged += OnCompositionScaleChanged;
            swapChainPanel.SizeChanged += OnSwapChainPanelSizeChanged;

            // Pointer and manipulation events handle image pan and zoom.
            swapChainPanel.PointerPressed += OnPointerPressed;
            swapChainPanel.PointerMoved += OnPointerMoved;
            swapChainPanel.PointerReleased += OnPointerReleased;
            swapChainPanel.PointerCanceled += OnPointerCanceled;
            swapChainPanel.PointerWheelChanged += OnPointerWheelChanged;

            gestureRecognizer = new GestureRecognizer();
            gestureRecognizer.ManipulationStarted += OnManipulationStarted;
            gestureRecognizer.ManipulationUpdated += OnManipulationUpdated;
            gestureRecognizer.ManipulationCompleted += OnManipulationCompleted;
            gestureRecognizer.GestureSettings =
                GestureSettings.ManipulationTranslateX |
                GestureSettings.ManipulationTranslateY |
                GestureSettings.ManipulationScale;

            viewModel = new RenderOptionsViewModel();

            // At this point we have access to the device and can create the device-dependent resources.
            renderer = new HDRImageViewerRenderer(swapChainPanel);

            UpdateDisplayACState(acInfo);
        }

        private void UpdateDisplayACState(AdvancedColorInfo newAcInfo)
        {
            AdvancedColorKind oldDispKind = AdvancedColorKind.StandardDynamicRange;
            if (dispInfo != null)
            {
                // dispInfo won't be available until the first image has been loaded.
                oldDispKind = dispInfo.CurrentAdvancedColorKind;
            }

            // TODO: Confirm that newAcInfo is never null. I believe this was needed in past versions for RS4 compat.
            dispInfo = newAcInfo;
            AdvancedColorKind newDispKind = dispInfo.CurrentAdvancedColorKind;
            DisplayACState.Text = UIStrings.LABEL_ACKIND + UIStrings.ConvertACKindToString(newDispKind);

            int maxcll = (int)dispInfo.MaxLuminanceInNits;

            if (maxcll == 0)
            {
                // Luminance value of 0 means that no valid data was provided by the display.
                DisplayPeakLuminance.Text = UIStrings.LABEL_PEAKLUMINANCE + UIStrings.LABEL_UNKNOWN;
            }
            else
            {
                DisplayPeakLuminance.Text = UIStrings.LABEL_PEAKLUMINANCE + maxcll.ToString() + UIStrings.LABEL_LUMINANCE_NITS;
            }

            if (oldDispKind == newDispKind)
            {
                // Some changes, such as peak luminance or SDR white level, don't need to reset rendering options.
                UpdateRenderOptions();
            }
            else
            {
                // If display has changed kind between SDR/HDR/WCG, we must reset all rendering options.
                UpdateDefaultRenderOptions();
            }
        }

        // Based on image and display parameters, choose the best rendering options.
        private void UpdateDefaultRenderOptions()
        {
            if (!isImageValid)
            {
                // Render options are only meaningful if an image is already loaded.
                return;
            }

            switch (imageInfo.imageKind)
            {
                case AdvancedColorKind.StandardDynamicRange:
                case AdvancedColorKind.WideColorGamut:
                default:
                    // SDR and WCG images don't need to be tonemapped.
                    RenderEffectCombo.SelectedIndex = 0; // See RenderOptions.h for which value this indicates.

                    // Manual brightness adjustment is only useful for HDR content.
                    // SDR and WCG content is adjusted by the OS-provided AdvancedColorInfo.SdrWhiteLevel parameter.
                    BrightnessAdjustSlider.Value = SdrBrightnessFormatter.BrightnessToSlider(1.0);
                    BrightnessAdjustPanel.Visibility = Visibility.Collapsed;
                    break;

                case AdvancedColorKind.HighDynamicRange:
                    // HDR images need to be tonemapped regardless of display kind.
                    RenderEffectCombo.SelectedIndex = 1; // See RenderOptions.h for which value this indicates.

                    // Manual brightness adjustment is useful for any HDR content.
                    BrightnessAdjustPanel.Visibility = Visibility.Visible;
                    break;
            }

            UpdateRenderOptions();
        }

        // Common method for updating options on the renderer.
        private void UpdateRenderOptions()
        {
            if ((renderer != null) && (RenderEffectCombo.SelectedItem != null))
            {
                var tm = (EffectOption)RenderEffectCombo.SelectedItem;

                renderer.SetRenderOptions(
                    tm.Kind,
                    (float)SdrBrightnessFormatter.SliderToBrightness(BrightnessAdjustSlider.Value),
                    dispInfo
                    );
            }
        }

        // Swap chain event handlers.

        private void OnSwapChainPanelSizeChanged(object sender, SizeChangedEventArgs e)
        {
            renderer.SetLogicalSize(e.NewSize);
            renderer.CreateWindowSizeDependentResources();
            renderer.Draw();
        }

        private void OnCompositionScaleChanged(SwapChainPanel sender, object args)
        {
            renderer.SetCompositionScale(sender.CompositionScaleX, sender.CompositionScaleY);
            renderer.CreateWindowSizeDependentResources();
            renderer.Draw();
        }

        // Display state event handlers.

        private void OnAdvancedColorInfoChanged(DisplayInformation sender, object args)
        {
            UpdateDisplayACState(sender.GetAdvancedColorInfo());
        }

        private void OnDisplayContentsInvalidated(DisplayInformation sender, object args)
        {
            renderer.ValidateDevice();
            renderer.CreateWindowSizeDependentResources();
            renderer.Draw();
        }

        private void OnOrientationChanged(DisplayInformation sender, object args)
        {
            renderer.SetCurrentOrientation(sender.CurrentOrientation);
            renderer.CreateWindowSizeDependentResources();
            renderer.Draw();
        }

        private void OnDpiChanged(DisplayInformation sender, object args)
        {
            renderer.SetDpi(sender.LogicalDpi);
            renderer.CreateWindowSizeDependentResources();
            renderer.Draw();
        }

        // Window event handlers.

        // ResizeCompleted is used to detect when the window has been moved between different displays.
        private void OnResizeCompleted(CoreWindow sender, object args)
        {
            UpdateRenderOptions();
        }

        private void OnVisibilityChanged(CoreWindow sender, VisibilityChangedEventArgs args)
        {
            isWindowVisible = args.Visible;
            if (isWindowVisible)
            {
                renderer.Draw();
            }
        }

        // Other event handlers.

        private void OnKeyUp(CoreWindow sender, KeyEventArgs args)
        {
            if (VirtualKey.H == args.VirtualKey)
            {
                if (Windows.UI.Xaml.Visibility.Collapsed == ControlsPanel.Visibility)
                {
                    ControlsPanel.Visibility = Windows.UI.Xaml.Visibility.Visible;
                }
                else
                {
                    ControlsPanel.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
                }
            }
            else if (VirtualKey.F == args.VirtualKey ||
                     VirtualKey.F11 == args.VirtualKey)
            {
                if (ApplicationView.GetForCurrentView().IsFullScreenMode)
                {
                    ApplicationView.GetForCurrentView().ExitFullScreenMode();
                }
                else
                {
                    ApplicationView.GetForCurrentView().TryEnterFullScreenMode();
                }
            }
            else if (VirtualKey.Escape == args.VirtualKey)
            {
                if (ApplicationView.GetForCurrentView().IsFullScreenMode)
                {
                    ApplicationView.GetForCurrentView().ExitFullScreenMode();
                }
            }
        }

        public async void LoadImageAsync(StorageFile imageFile)
        {
            isImageValid = false;
            BrightnessAdjustSlider.IsEnabled = false;
            RenderEffectCombo.IsEnabled = false;

            bool useDirectXTex = false;

            var type = imageFile.FileType.ToLowerInvariant();
            if (type == ".hdr" ||
                type == ".exr" ||
                type == ".dds")
            {
                useDirectXTex = true;
            }

            ImageInfo info;

            if (useDirectXTex)
            {
                // For formats that are loaded by DirectXTex, we must use a file path from the temporary folder.
                imageFile = await imageFile.CopyAsync(
                        ApplicationData.Current.TemporaryFolder,
                        imageFile.Name,
                        NameCollisionOption.ReplaceExisting);

                info = renderer.LoadImageFromDirectXTex(imageFile.Path, type);
            }
            else
            {
                info = renderer.LoadImageFromWic(await imageFile.OpenAsync(FileAccessMode.Read));
            }

            if (info.isValid == false)
            {
                // Exit before any of the current image state is modified.
                var dialog = new ContentDialog
                {
                    Title = imageFile.Name,
                    Content = UIStrings.DIALOG_LOADFAILED,
                    CloseButtonText = UIStrings.DIALOG_OK
                };

                await dialog.ShowAsync();

                return;
            }

            imageInfo = info;

            renderer.CreateImageDependentResources();
            imageCLL = renderer.FitImageToWindow(true); // On first load of image, need to generate HDR metadata.

            ApplicationView.GetForCurrentView().Title = imageFile.Name;
            ImageACKind.Text = UIStrings.LABEL_ACKIND + UIStrings.ConvertACKindToString(imageInfo.imageKind);
            ImageHasProfile.Text = UIStrings.LABEL_COLORPROFILE + (imageInfo.numProfiles > 0 ? UIStrings.LABEL_YES : UIStrings.LABEL_NO);
            ImageBitDepth.Text = UIStrings.LABEL_BITDEPTH + imageInfo.bitsPerChannel;
            ImageIsFloat.Text = UIStrings.LABEL_FLOAT + (imageInfo.isFloat ? UIStrings.LABEL_YES : UIStrings.LABEL_NO);

            // TODO: Should we treat the 0 nit case as N/A as well? A fully black image would be known to have 0 CLL, which is valid...
            if (imageCLL.maxNits < 0.0f)
            {
                ImageMaxCLL.Text = UIStrings.LABEL_MAXCLL + UIStrings.LABEL_NA;
            }
            else
            {
                ImageMaxCLL.Text = UIStrings.LABEL_MAXCLL + imageCLL.maxNits.ToString("N1") + UIStrings.LABEL_LUMINANCE_NITS;
            }

            if (imageCLL.medNits < 0.0f)
            {
                ImageMedianCLL.Text = UIStrings.LABEL_MEDCLL + UIStrings.LABEL_NA;
            }
            else
            {
                ImageMedianCLL.Text = UIStrings.LABEL_MEDCLL + imageCLL.medNits.ToString("N1") + UIStrings.LABEL_LUMINANCE_NITS;
            }

            // Image loading is done at this point.
            isImageValid = true;
            BrightnessAdjustSlider.IsEnabled = true;
            RenderEffectCombo.IsEnabled = true;

            if (imageInfo.imageKind == AdvancedColorKind.HighDynamicRange)
            {
                ExportImageButton.IsEnabled = true;
            }
            else
            {
                ExportImageButton.IsEnabled = false;
            }

            UpdateDefaultRenderOptions();
        }

        private async Task ExportImageToSdrAsync(StorageFile file)
        {
            Guid wicFormat;
            if (file.FileType.Equals(".jpg", StringComparison.OrdinalIgnoreCase)) // TODO: Remove this hardcoded constant.
            {
                wicFormat = DirectXCppConstants.GUID_ContainerFormatJpeg;
            }
            else
            {
                wicFormat = DirectXCppConstants.GUID_ContainerFormatPng;
            }

            var ras = await file.OpenAsync(FileAccessMode.ReadWrite);
            renderer.ExportImageToSdr(ras, wicFormat);
        }

        // Saves the current state of the app for suspend and terminate events.
        public void SaveInternalState(IPropertySet state)
        {
            renderer.Trim();
        }

        // Loads the current state of the app for resume events.
        public void LoadInternalState(IPropertySet state)
        {
        }

        // UI Element event handlers.

        private async void ExportImageButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                CommitButtonText = "Export image to SDR"
            };

            foreach (var format in UIStrings.FILEFORMATS_SAVE)
            {
                picker.FileTypeChoices.Add(format);
            }

            var pickedFile = await picker.PickSaveFileAsync();
            if (pickedFile != null)
            {
                await ExportImageToSdrAsync(pickedFile);
            }
        }

        private async void OpenImageButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };

            foreach (var ext in UIStrings.FILEFORMATS_OPEN)
            {
                picker.FileTypeFilter.Add(ext);
            }

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                LoadImageAsync(file);
            }
        }

        private void BrightnessAdjustSlider_Changed(object sender, RangeBaseValueChangedEventArgs e)
        {
            UpdateRenderOptions();
        }

        private void RenderEffectCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            UpdateRenderOptions();
        }

        // Pointer input event handlers.

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            swapChainPanel.CapturePointer(e.Pointer);
            gestureRecognizer.ProcessDownEvent(e.GetCurrentPoint(swapChainPanel));
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            gestureRecognizer.ProcessMoveEvents(e.GetIntermediatePoints(swapChainPanel));
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            gestureRecognizer.ProcessUpEvent(e.GetCurrentPoint(swapChainPanel));
            swapChainPanel.ReleasePointerCapture(e.Pointer);
        }

        private void OnPointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            gestureRecognizer.CompleteGesture();
            swapChainPanel.ReleasePointerCapture(e.Pointer);
        }

        private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            // Passing isControlKeyDown = true causes the wheel delta to be treated as scrolling.
            gestureRecognizer.ProcessMouseWheelEvent(e.GetCurrentPoint(swapChainPanel), false, true);
        }

        private void OnManipulationStarted(GestureRecognizer sender, ManipulationStartedEventArgs args)
        {
        }
        private void OnManipulationUpdated(GestureRecognizer sender, ManipulationUpdatedEventArgs args)
        {
            renderer.UpdateManipulationState(args);
        }

        private void OnManipulationCompleted(GestureRecognizer sender, ManipulationCompletedEventArgs args)
        {
        }
    }
}