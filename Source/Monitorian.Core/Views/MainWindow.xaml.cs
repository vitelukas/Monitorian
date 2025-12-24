using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Text;

using Monitorian.Core.Helper;
using Monitorian.Core.Models;
using Monitorian.Core.ViewModels;
using ScreenFrame.Movers;

namespace Monitorian.Core.Views;

public partial class MainWindow : Window
{
	private readonly StickWindowMover _mover;
	public MainWindowViewModel ViewModel => (MainWindowViewModel)this.DataContext;

	public MainWindow(AppControllerCore controller)
	{
		LanguageService.Switch();

		InitializeComponent();

		this.DataContext = new MainWindowViewModel(controller);

		_mover = new StickWindowMover(this, controller.NotifyIconContainer.NotifyIcon)
		{
			KeepsDistance = true
		};
		_mover.ForegroundWindowChanged += OnDeactivated;

		controller.WindowPainter.Add(this);
		controller.WindowPainter.ThemeChanged += (_, _) =>
		{
			ViewModel.MonitorsView.Refresh();
		};
		//controller.WindowPainter.AccentColorChanged += (_, _) =>
		//{
		//};
	}

	public override void OnApplyTemplate()
	{
		base.OnApplyTemplate();

		CheckDefaultHeights();

		BindingOperations.SetBinding(
			this,
			UsesLargeElementsProperty,
			new Binding(nameof(SettingsCore.UsesLargeElements))
			{
				Source = ViewModel.Settings,
				Mode = BindingMode.OneWay
			});

		//this.InvalidateProperty(UsesLargeElementsProperty);
	}

	protected override void OnClosed(EventArgs e)
	{
		BindingOperations.ClearBinding(
			this,
			UsesLargeElementsProperty);

		base.OnClosed(e);
	}

	#region Elements

	private const double ShrinkFactor = 0.64;
	private Dictionary<string, double> _defaultHeights;
	private const string SliderHeightName = "SliderHeight";

	private void CheckDefaultHeights()
	{
		_defaultHeights = this.Resources.Cast<DictionaryEntry>()
			.Where(x => (x.Key is string key) && key.EndsWith("Height", StringComparison.Ordinal))
			.Where(x => x.Value is double height and > 0D)
			.ToDictionary(x => (string)x.Key, x => (double)x.Value);
	}

	public bool UsesLargeElements
	{
		get { return (bool)GetValue(UsesLargeElementsProperty); }
		set { SetValue(UsesLargeElementsProperty, value); }
	}
	public static readonly DependencyProperty UsesLargeElementsProperty =
		DependencyProperty.Register(
			"UsesLargeElements",
			typeof(bool),
			typeof(MainWindow),
			new PropertyMetadata(
				true,
				(d, e) =>
				{
					// Setting the same value will not trigger calling this method.

					var window = (MainWindow)d;
					if (window._defaultHeights is null)
						return;

					var factor = (bool)e.NewValue ? 1D : ShrinkFactor;

					foreach (var (key, value) in window._defaultHeights)
					{
						var buffer = value * factor;
						if (key == SliderHeightName)
							buffer = Math.Ceiling(buffer / 4) * 4;

						window.Resources[key] = buffer;
					}
				}));

	#endregion

	#region Show/Hide

	public bool IsForeground => _mover.IsForeground();

	public void ShowForeground()
	{
		try
		{
			this.Topmost = true;

			// When a window is deactivated, a focused element will lose focus and usually,
			// no element will have focus until the window is activated again and the last focused
			// element automatically gets focus back. Therefore, in usual case, no focused element
			// exists before Window.Show method is called. However, it is possible to set focus on
			// an element during window is not active and such focused element is found here.
			// The issue is that such focused element will lose focus because the element which had
			// focus before the window was deactivated will restore focus even though any other
			// element has focus. To prevent this unintended change of focus, it is necessary to
			// set focus back on the element which has focus before Window.Show method is called.
			var currentFocusedElement = FocusManager.GetFocusedElement(this);

			base.Show();

			if (currentFocusedElement is not null)
			{
				var restoredFocusedElement = FocusManager.GetFocusedElement(this);
				if (restoredFocusedElement != currentFocusedElement)
					FocusManager.SetFocusedElement(this, currentFocusedElement);
			}
		}
		catch (ArgumentException ex) when ((uint)ex.HResult is 0x80070057)
		{
			// Window.Show method can cause ArgumentException when internally calling
			// CompositionTarget.SetRootVisual method.
		}
		finally
		{
			this.Topmost = false;
		}
	}

	public void ShowUnnoticed()
	{
		var width = this.Width;
		var height = this.Height;
		var sizeToContent = this.SizeToContent;
		try
		{
			// Set window size as small as possible to make it almost unnoticed.
			this.Width = 1;
			this.Height = 1;
			this.SizeToContent = SizeToContent.Manual;

			base.Show();
			this.Hide();
		}
		finally
		{
			// Restore window size.
			this.Width = width;
			this.Height = height;
			this.SizeToContent = sizeToContent;
		}
	}

	public bool CanBeShown => (_preventionTime < DateTimeOffset.Now);
	private DateTimeOffset _preventionTime;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    private bool IsTaskbarActive()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        const int nChars = 256;
        StringBuilder className = new StringBuilder(nChars);
        GetClassName(hwnd, className, nChars);

        string name = className.ToString();

        // Check if the currently active window from this list
        return name == "Shell_TrayWnd" || 
               name == "Shell_SecondaryTrayWnd" || 
               name == "NotifyIconOverflowWindow" || 
               name == "TopLevelWindowForOverflowXamlIsland" ||
               // The Desktop (Progman/WorkerW)
               // When the Overflow menu closes, focus often falls back to the Desktop
               // We instead consider the Desktop "safe" and check Mouse Position instead
               name == "Progman" ||
               name == "WorkerW";
    }

	private bool IsMouseSafelyOverWindow()
    {
        // Get mouse coordinates
        if (!GetCursorPos(out POINT p)) return false;

        // Get window coordinates
        // Use PointToScreen to handle DPI scaling correctly
        Point topLeft;
        Point bottomRight;
        try 
        {
             topLeft = this.PointToScreen(new Point(0, 0));
             bottomRight = this.PointToScreen(new Point(this.ActualWidth, this.ActualHeight));
        }
        catch 
		{ 
			return false; // Window might not be visible yet
		}

        // Add a 10-pixel "buffer" zone around the window to handle 
        // the tiny gap between the tray icon and the window.
        int buffer = 10;
        // Check if the mouse is inside the window
        return (p.X >= topLeft.X - buffer && p.X <= bottomRight.X + buffer &&
                p.Y >= topLeft.Y - buffer && p.Y <= bottomRight.Y + buffer);
    }

	private void OnDeactivated(object sender, EventArgs e)
    {
        HandleDeactivation();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        HandleDeactivation();
    }

	private void HandleDeactivation()
    {
        // If the user's mouse is hovering over the Monitorian window,
        // ignore any focus loss (Taskbar, Desktop, Overflow menu)
        if (IsMouseSafelyOverWindow())
        {
            this.Activate(); 
            return;
        }

        // If the mouse is NOT over the window, check if the Taskbar or Overflow
        // is active. If so, we still keep it open (waiting for mouse to move).
        if (IsTaskbarActive())
        {
            this.Activate();
            return;
        }

        // If mouse is outside of the main-window => close it
        ProceedHide();
    }
	
	private void ProceedHide()
	{
		if (this.Visibility is not Visibility.Visible)
			return;

		// Compare time to prevent hiding procedure from repeating.
		if (_preventionTime > DateTimeOffset.Now)
			return;

		ViewModel.Deactivate();

		// Set time to prevent this window from being shown unintentionally.
		_preventionTime = DateTimeOffset.Now + TimeSpan.FromSeconds(0.2);

		ClearHide();
	}

	public async void ClearHide()
	{
		// Clear focus.
		FocusManager.SetFocusedElement(this, null);

		// Wait for this window to be refreshed before being hidden.
		await Task.Delay(TimeSpan.FromSeconds(0.1));

		this.Hide();
	}

	#endregion
}