﻿// Copyright © 2010-2013 The CefSharp Project. All rights reserved.
//
// Use of this source code is governed by a BSD-style license that can be found in the LICENSE file.

using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using CefSharp.Internals;

namespace CefSharp.Wpf
{
    public class WebView : ContentControl, IRenderWebBrowser, IWpfWebBrowser
    {
        private HwndSource source;
        private HwndSourceHook sourceHook;
        private DispatcherTimer tooltipTimer;
        private readonly ToolTip toolTip;
        private ManagedCefBrowserAdapter managedCefBrowserAdapter;
        private bool isOffscreenBrowserCreated;
        private bool ignoreUriChange;

        private Image image;
        private Image popupImage;
        private Popup popup;

        public BrowserSettings BrowserSettings { get; set; }
        public bool IsBrowserInitialized { get; private set; }
        public IJsDialogHandler JsDialogHandler { get; set; }
        public IKeyboardHandler KeyboardHandler { get; set; }
        public IRequestHandler RequestHandler { get; set; }
        public ILifeSpanHandler LifeSpanHandler { get; set; }

        public event ConsoleMessageEventHandler ConsoleMessage;
        public event LoadCompletedEventHandler LoadCompleted;
        public event LoadErrorEventHandler LoadError;

        public ICommand BackCommand { get; private set; }
        public ICommand ForwardCommand { get; private set; }
        public ICommand ReloadCommand { get; private set; }

        public bool CanGoBack { get; private set; }
        public bool CanGoForward { get; private set; }
        public bool CanReload { get; private set; }

        public int BytesPerPixel
        {
            get { return PixelFormat.BitsPerPixel / 8; }
        }

        int IRenderWebBrowser.Width
        {
            get { return (int)ActualWidth; }
        }

        int IRenderWebBrowser.Height
        {
            get { return (int)ActualHeight; }
        }

        private static PixelFormat PixelFormat
        {
            get { return PixelFormats.Bgra32; }
        }

        #region Address dependency property

        public string Address
        {
            get { return (string)GetValue(AddressProperty); }
            set { SetValue(AddressProperty, value); }
        }
        
        public static readonly DependencyProperty AddressProperty =
            DependencyProperty.Register("Address", typeof(string), typeof(WebView),
                                        new UIPropertyMetadata( null, OnAdressChanged ) );

        private static void OnAdressChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        {
            WebView owner = (WebView) sender;
            string oldValue = (string) args.OldValue;
            string newValue = (string) args.NewValue;

            owner.OnAddressChanged( oldValue, newValue );
        }

        protected virtual void OnAddressChanged( string oldValue, string newValue )
        {
            if (ignoreUriChange)
            {
                return;
            }

            if (!Cef.IsInitialized &&
                !Cef.Initialize())
            {
                throw new InvalidOperationException("Cef::Initialize() failed");
            }

            // TODO: Consider making the delay here configurable.
            tooltipTimer = new DispatcherTimer(
                TimeSpan.FromSeconds(0.5),
                DispatcherPriority.Render,
                OnTooltipTimerTick,
                Dispatcher
            );

            if (isOffscreenBrowserCreated)
            {
                managedCefBrowserAdapter.LoadUrl(Address);
            }
            else
            {
                InitializeCefAdapter();
            }
        }

        #endregion Address dependency property

        #region IsLoading dependency property

        public bool IsLoading
        {
            get { return (bool)GetValue(IsLoadingProperty); }
            set { SetValue(IsLoadingProperty, value); }
        }

        public static readonly DependencyProperty IsLoadingProperty =
            DependencyProperty.Register("IsLoading", typeof(bool), typeof(WebView), new PropertyMetadata(false));

        #endregion IsLoading dependency property

        #region Title dependency property

        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register("Title", typeof(string), typeof(WebView), new PropertyMetadata(defaultValue: null));

        #endregion Title dependency property

        #region TooltipText dependency property

        public string TooltipText
        {
            get { return (string)GetValue(TooltipTextProperty); }
            set { SetValue(TooltipTextProperty, value); }
        }

        public static readonly DependencyProperty TooltipTextProperty =
            DependencyProperty.Register("TooltipText", typeof(string), typeof(WebView), new PropertyMetadata(null, (sender, e) => ((WebView)sender).OnTooltipTextChanged()));

        private void OnTooltipTextChanged()
        {
            tooltipTimer.Stop();

            if (String.IsNullOrEmpty(TooltipText))
            {
                Dispatcher.BeginInvoke((Action)(() => UpdateTooltip(null)), DispatcherPriority.Render);
            }
            else
            {
                tooltipTimer.Start();
            }
        }

        #endregion

        #region WebBrowser dependency property

        public IWebBrowser WebBrowser
        {
            get { return (IWebBrowser)GetValue(WebBrowserProperty); }
            set { SetValue(WebBrowserProperty, value); }
        }

        public static readonly DependencyProperty WebBrowserProperty =
            DependencyProperty.Register("WebBrowser", typeof(IWebBrowser), typeof(WebView), new UIPropertyMetadata(defaultValue: null));

        #endregion WebBrowser dependency property

        static WebView()
        {
            Application.Current.Exit += OnApplicationExit;
        }

        public WebView()
        {
            Focusable = true;
            FocusVisualStyle = null;
            IsTabStop = true;

            Dispatcher.BeginInvoke((Action)(() => WebBrowser = this));

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

            this.IsVisibleChanged += OnIsVisibleChanged;

            ToolTip = toolTip = new ToolTip();
            toolTip.StaysOpen = true;
            toolTip.Visibility = Visibility.Collapsed;
            toolTip.Closed += OnTooltipClosed;


            BackCommand = new DelegateCommand(Back, () => CanGoBack );
            ForwardCommand = new DelegateCommand(Forward, () => CanGoForward);
            ReloadCommand = new DelegateCommand(Reload, () => CanReload);
        }

        private void DoInUi(Action action, DispatcherPriority priority = DispatcherPriority.DataBind )
        {
            if ( Dispatcher.CheckAccess() )
            {
                action();
            }
            else if ( !Dispatcher.HasShutdownStarted )
            {
                Dispatcher.BeginInvoke(action, priority );
            }
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
        {
            //(HwndSource) PresentationSource.FromVisual( this ); will fail if the control was not rendered yet so we need to try initialize if visibility changes 
            InitializeCefAdapter();
        }


        private static void OnApplicationExit(object sender, ExitEventArgs e)
        {
            Cef.Shutdown();
        }


        private void OnLoaded( object sender, RoutedEventArgs routedEventArgs )
        {
            InitializeCefAdapter();
        }

        public void OnUnloaded(object sender, RoutedEventArgs routedEventArgs)
        {
            RemoveSourceHook();
            ShutdownManagedCefBrowserAdapter();
        }

        private void ShutdownManagedCefBrowserAdapter()
        {
            var temp = managedCefBrowserAdapter;

            if (temp == null)
            {
                return;
            }

            managedCefBrowserAdapter = null;
            isOffscreenBrowserCreated = false;
            temp.Close();
            temp.Dispose();
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            InitializeCefAdapter();

            Content = image = new Image();
            popup = CreatePopup();

            RenderOptions.SetBitmapScalingMode( image, BitmapScalingMode.NearestNeighbor );

            image.Stretch = Stretch.None;
            image.HorizontalAlignment = HorizontalAlignment.Left;
            image.VerticalAlignment = VerticalAlignment.Top;
        }

        private void InitializeCefAdapter()
        {
            if ( isOffscreenBrowserCreated )
            {
                return;
            }
            
            if (!AddSourceHook())
            {
                return;
            }

            if (!CreateOffscreenBrowser())
            {
                return;
            }

            isOffscreenBrowserCreated = true;
        }

        private Popup CreatePopup()
        {
            var popup = new Popup
            {
                Child = popupImage = new Image(),
                PlacementTarget = this,
                Placement = PlacementMode.Relative
            };

            return popup;
        }

        private bool CreateOffscreenBrowser()
        {
            if ( Address == null || source == null )
            {
                return false;
            }

            if (isOffscreenBrowserCreated)
            {
                return true;
            }

            managedCefBrowserAdapter = new ManagedCefBrowserAdapter( this );
            managedCefBrowserAdapter.CreateOffscreenBrowser( BrowserSettings ?? new BrowserSettings(), source.Handle, Address );

            return true;
        }

        private bool AddSourceHook()
        {
            if (source != null)
            {
                return true;
            }

            source = (HwndSource)PresentationSource.FromVisual(this);

            if (source != null)
            {
                sourceHook = SourceHook;
                source.AddHook(sourceHook);
                return true;
            }

            return false;
        }

        private void RemoveSourceHook()
        {
            if (source != null &&
                sourceHook != null)
            {
                source.RemoveHook(sourceHook);
                source = null;
            }
        }

        private IntPtr SourceHook(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            handled = false;

            switch ((WM)message)
            {
                case WM.SYSCHAR:
                case WM.SYSKEYDOWN:
                case WM.SYSKEYUP:
                case WM.KEYDOWN:
                case WM.KEYUP:
                case WM.CHAR:
                case WM.IME_CHAR:
                    if (!IsFocused)
                    {
                        break;
                    }

                    if (message == (int)WM.SYSKEYDOWN &&
                        wParam.ToInt32() == KeyInterop.VirtualKeyFromKey(Key.F4))
                    {
                        // We don't want CEF to receive this event (and mark it as handled), since that makes it impossible to
                        // shut down a CefSharp-based app by pressing Alt-F4, which is kind of bad.
                        return IntPtr.Zero;
                    }

                    if (managedCefBrowserAdapter.SendKeyEvent(message, wParam.ToInt32()))
                    {
                        handled = true;
                    }

                    break;
            }

            return IntPtr.Zero;
        }

        protected override Size ArrangeOverride(Size arrangeBounds)
        {
            var size = base.ArrangeOverride(arrangeBounds);
            var newWidth = size.Width;
            var newHeight = size.Height;

            if (newWidth > 0 && newHeight > 0 && isOffscreenBrowserCreated )
            {
                managedCefBrowserAdapter.WasResized();
            }

            return size;
        }

        public void InvokeRenderAsync( BitmapInfo bitmapInfo)
        {
            DoInUi(() => SetBitmap(bitmapInfo), DispatcherPriority.Render);
        }

        public void SetAddress(string address)
        {
            DoInUi(() =>
            {
                ignoreUriChange = true;
                Address = address;
                ignoreUriChange = false;

                // The tooltip should obviously also be reset (and hidden) when the address changes.
                TooltipText = null;
            });
        }

        public void SetIsLoading(bool isLoading)
        {
            DoInUi(() => IsLoading = isLoading);
        }

        public void SetNavState(bool canGoBack, bool canGoForward, bool canReload)
        {
            CanGoBack = canGoBack;
            CanGoForward = canGoForward;
            CanReload = canReload;

            RaiseCommandsCanExecuteChanged();
        }

        private void RaiseCommandsCanExecuteChanged()
        {
            ((DelegateCommand)BackCommand).RaiseCanExecuteChanged();
            ((DelegateCommand)ForwardCommand).RaiseCanExecuteChanged();
            ((DelegateCommand)ReloadCommand).RaiseCanExecuteChanged();
        }

        public void SetTitle(string title)
        {
            DoInUi(() => Title = title);
        }

        public void SetTooltipText(string tooltipText)
        {
            DoInUi(() => TooltipText = tooltipText);
        }
        
        public void SetPopupSizeAndPosition(int width, int height, int x, int y)
        {
            DoInUi(() => 
            {
                popup.Width = width;
                popup.Height = height;

                var popupOffset = new Point(x, y);
                // TODO: Port over this from CefSharp1.
                //if (popupOffsetTransform != null) 
                //{
                //    popupOffset = popupOffsetTransform->GeneralTransform::Transform(popupOffset);
                //}

                popup.HorizontalOffset = popupOffset.X;
                popup.VerticalOffset = popupOffset.Y;
            });
        }

        public void SetPopupIsOpen(bool isOpen)
        {
            DoInUi(() => popup.IsOpen = isOpen);
        }

        private void OnTooltipTimerTick(object sender, EventArgs e)
        {
            tooltipTimer.Stop();

            UpdateTooltip(TooltipText);
        }

        private void OnTooltipClosed(object sender, RoutedEventArgs e)
        {
            toolTip.Visibility = Visibility.Collapsed;

            // Set Placement to something other than PlacementMode.Mouse, so that when we re-show the tooltip in
            // UpdateTooltip(), the tooltip will be repositioned to the new mouse point.
            toolTip.Placement = PlacementMode.Absolute;
        }

        private void UpdateTooltip(string text)
        {
            if (String.IsNullOrEmpty(text))
            {
                toolTip.IsOpen = false;
            }
            else
            {
                toolTip.Content = text;
                toolTip.Placement = PlacementMode.Mouse;
                toolTip.Visibility = Visibility.Visible;
                toolTip.IsOpen = true;
            }
        }

        protected override void OnGotFocus(RoutedEventArgs e)
        {
            if (isOffscreenBrowserCreated)
            {
                managedCefBrowserAdapter.SendFocusEvent( true );
            }

            base.OnGotFocus(e);
        }

        protected override void OnLostFocus(RoutedEventArgs e)
        {
            if ( isOffscreenBrowserCreated )
            {
                managedCefBrowserAdapter.SendFocusEvent(false);
            }

            base.OnLostFocus(e);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            OnPreviewKey(e);
        }

        protected override void OnPreviewKeyUp(KeyEventArgs e)
        {
            OnPreviewKey(e);
        }

        private void OnPreviewKey(KeyEventArgs e)
        {
            // For some reason, not all kinds of keypresses triggers the appropriate WM_ messages handled by our SourceHook, so
            // we have to handle these extra keys here. Hooking the Tab key like this makes the tab focusing in essence work like
            // KeyboardNavigation.TabNavigation="Cycle"; you will never be able to Tab out of the web browser control.

            if (e.Key == Key.Tab ||
                new[] { Key.Left, Key.Right, Key.Up, Key.Down }.Contains(e.Key))
            {
                var message = (int)(e.IsDown ? WM.KEYDOWN : WM.KEYUP);
                var virtualKey = KeyInterop.VirtualKeyFromKey(e.Key);
                managedCefBrowserAdapter.SendKeyEvent(message, virtualKey);
                e.Handled = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var point = e.GetPosition(this);
            managedCefBrowserAdapter.OnMouseMove((int)point.X, (int)point.Y, mouseLeave: false);
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            var point = e.GetPosition(this);
            
            if (isOffscreenBrowserCreated)
            {
                managedCefBrowserAdapter.OnMouseWheel(
                    (int) point.X,
                    (int) point.Y,
                    deltaX: 0,
                    deltaY: e.Delta
                );
            }
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            Focus();
            OnMouseButton(e);
            Mouse.Capture(this);
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            OnMouseButton(e);
            Mouse.Capture(null);
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            if (isOffscreenBrowserCreated)
            {
                managedCefBrowserAdapter.OnMouseMove( 0, 0, mouseLeave: true );
            }
        }

        private void OnMouseButton(MouseButtonEventArgs e)
        {
            MouseButtonType mouseButtonType;

            switch (e.ChangedButton)
            {
                case MouseButton.Left:
                    mouseButtonType = MouseButtonType.Left;
                    break;

                case MouseButton.Middle:
                    mouseButtonType = MouseButtonType.Middle;
                    break;

                case MouseButton.Right:
                    mouseButtonType = MouseButtonType.Right;
                    break;

                default:
                    return;
            }

            var mouseUp = (e.ButtonState == MouseButtonState.Released);

            var point = e.GetPosition(this);

            if (isOffscreenBrowserCreated)
            {
                managedCefBrowserAdapter.OnMouseButton( (int) point.X, (int) point.Y, mouseButtonType, mouseUp, e.ClickCount );
            }
        }

        public void OnInitialized()
        {
        }

        public void Load(string url)
        {
            throw new NotImplementedException();
        }

        public void LoadHtml(string html, string url)
        {
            if (isOffscreenBrowserCreated)
            {
                managedCefBrowserAdapter.LoadHtml( html, url );
            }
        }

        private void Back()
        {
            if (isOffscreenBrowserCreated)
            {
                managedCefBrowserAdapter.GoBack();
            }
        }

        private void Forward()
        {
            if (isOffscreenBrowserCreated)
            {
                managedCefBrowserAdapter.GoForward();
            }
        }
        
        public void Reload()
        {
            if (isOffscreenBrowserCreated)
            {
                managedCefBrowserAdapter.Reload();
            }
        }

        public void ShowDevTools()
        {
            // TODO: Do something about this one.
            var devToolsUrl = managedCefBrowserAdapter.DevToolsUrl;
            throw new NotImplementedException();
        }

        public void CloseDevTools()
        {
            throw new NotImplementedException();
        }

        public void OnFrameLoadStart(string url)
        {
        }

        public void OnFrameLoadEnd(string url)
        {
            if (LoadCompleted != null)
            {
                LoadCompleted(this, new LoadCompletedEventArgs(url));
            }
        }

        public void OnTakeFocus(bool next)
        {
            throw new NotImplementedException();
        }

        public void OnConsoleMessage(string message, string source, int line)
        {
            if (ConsoleMessage != null)
            {
                ConsoleMessage(this, new ConsoleMessageEventArgs(message, source, line));
            }
        }

        public void OnLoadError(string url, CefErrorCode errorCode, string errorText)
        {
            if (LoadError != null)
            {
                LoadError(url, errorCode, errorText);
            }
        }

        public void RegisterJsObject(string name, object objectToBind)
        {
            throw new NotImplementedException();
        }

        public IDictionary<string, object> BoundObjects { get; private set; }

        public void ExecuteScriptAsync(string script)
        {
            if (isOffscreenBrowserCreated)
            {
                managedCefBrowserAdapter.ExecuteScriptAsync( script );
            }
        }

        public object EvaluateScript(string script)
        {
            return EvaluateScript(script, timeout: null);
        }

        public object EvaluateScript(string script, TimeSpan? timeout)
        {
            if (timeout == null)
            {
                timeout = TimeSpan.MaxValue;
            }

            if (isOffscreenBrowserCreated)
            {
                return managedCefBrowserAdapter.EvaluateScript( script, timeout.Value );
            }
            else
            {
                return null;
            }
        }

        public void SetCursor(IntPtr handle)
        {
            DoInUi(() => Cursor = CursorInteropHelper.Create(new SafeFileHandle(handle, ownsHandle: false)));
        }

        public void ClearBitmap(BitmapInfo bitmapInfo)
        {
            lock (bitmapInfo._bitmapLock)
            {
                bitmapInfo.InteropBitmap = null;
            }
        }

        public void SetBitmap(BitmapInfo bitmapInfo)
        {
            lock (bitmapInfo._bitmapLock)
            {
                if (bitmapInfo.IsPopup)
                {
                    bitmapInfo.InteropBitmap = SetBitmapHelper(bitmapInfo, (InteropBitmap)bitmapInfo.InteropBitmap, bitmap => popupImage.Source = bitmap);
                }
                else
                {
                    bitmapInfo.InteropBitmap = SetBitmapHelper(bitmapInfo, (InteropBitmap)bitmapInfo.InteropBitmap, bitmap => image.Source = bitmap);
                }
            }
        }

        private object SetBitmapHelper(BitmapInfo bitmapInfo, InteropBitmap bitmap, Action<InteropBitmap> imageSourceSetter)
        {
            if (bitmap == null)
            {
                imageSourceSetter(null);
                GC.Collect(1);

                var stride = bitmapInfo.Width * BytesPerPixel;

                bitmap = (InteropBitmap)Imaging.CreateBitmapSourceFromMemorySection(bitmapInfo.FileMappingHandle,
                    bitmapInfo.Width, bitmapInfo.Height, PixelFormat, stride, 0);
                imageSourceSetter(bitmap);
            }

            bitmap.Invalidate();

            return bitmap;
        }

        public void ViewSource()
        {
            if (isOffscreenBrowserCreated)
            {
                managedCefBrowserAdapter.ViewSource();
            }
        }
    }
}