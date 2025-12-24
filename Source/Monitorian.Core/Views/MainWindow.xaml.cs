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

	// Remember when the window opened
	private DateTime _lastShowTime;

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
			// Reset timer: mark the exact time the window appeared
        	_lastShowTime = DateTime.Now;

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

    private bool IsTaskbarActive()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        const int nChars = 256;
        StringBuilder className = new StringBuilder(nChars);
        GetClassName(hwnd, className, nChars);

        string name = className.ToString();
        // Check if the currently active window from this list
		return name == "Shell_TrayWnd" ||                  		// Main Taskbar
               name == "Shell_SecondaryTrayWnd" ||         		// Second Monitor Taskbar
               name == "NotifyIconOverflowWindow" ||       		// Hidden Icons Menu (Win 10)
               name == "TopLevelWindowForOverflowXamlIsland"; 	// Hidden Icons Menu (Win 11)
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
        // Time-based Immunity (0.5 seconds)
        // If the main-window just opened, ignore ALL focus loss
        // Handles the chaotic moment when the "Hidden Icons" menu closes
		// 	(in situation when the Monitorian tray icon is inside the Hidden Icons Menu and not directly on taskbar)
        if ((DateTime.Now - _lastShowTime).TotalMilliseconds < 500)
        {
            this.Activate(); // Force stay open
            return;
        }

        // Class-based Immunity
        // If the Taskbar or Icon Overflow menu stole focus, stay open
        if (IsTaskbarActive())
        {
            this.Activate();
            return;
        }

        // Otherwise, close normally
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