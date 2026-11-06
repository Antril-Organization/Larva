using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using static Larva.Setup.Native;

namespace Larva.Setup;

/// <summary>
/// The setup as a wizard: welcome, destination and options, ready to install (a summary, an upgrade, the optional
/// system components), installing (with a progress bar), done. One Win32 window whose controls each page shows or
/// hides; the header, the welcome panel and the brand in the bottom band are painted.
/// </summary>
internal sealed unsafe class Wizard
{
    private enum Page { Welcome, Options, Ready, Installing, Done }

    // Enter sends IDOK (1) and Esc IDCANCEL (2): Next and Cancel.
    private const int IdNext = 1, IdCancel = 2, IdBack = 3, IdBrowse = 10, IdPath = 11, IdApps = 12, IdHive = 15, IdTypes = 16, IdFolder = 13, IdOpen = 14;
    private const int IdBody = 20, IdFolderLabel = 21, IdSpace = 22, IdFree = 23, IdProgress = 30, IdStatus = 31;
    private const uint WM_PROGRESS = WM_APP, WM_INSTALLED = WM_APP + 1;
    private const int EN_CHANGE = 0x300, MB_RETRYCANCEL = 5, IDRETRY = 4;

    // The layout, at 96 DPI.
    private const int Width = 600, Height = 420, Band = 362, Header = 70, Panel = 180;
    private static readonly uint Accent = Rgb(0x1E, 0x6B, 0x33), Gray = Rgb(0xF3, 0xF3, 0xF3), LineColor = Rgb(0xDC, 0xDC, 0xDC);
    private static readonly uint Dim = Rgb(0x5A, 0x5A, 0x5A), Faint = Rgb(0x9A, 0x9A, 0x9A), Black = Rgb(0x1A, 0x1A, 0x1A), White = Rgb(0xFF, 0xFF, 0xFF);
    private static readonly uint Pale = Rgb(0xCF, 0xEB, 0xD3);

    private static Wizard? current;

    private readonly Texts t = Texts.Current;
    private readonly string? testOut;
    private readonly int dpi = (int)Math.Max(96, GetDpiForSystem());
    private readonly string version = Installer.Version;
    private readonly long payloadSize = Installer.PayloadSize();
    private readonly string? previousVersion = Installer.InstalledVersion();
    private readonly string? previousFolder = Installer.InstalledFolder();
    private nint instance, hwnd, font, bold, big, bigIcon, smallIcon;
    private nint whiteBrush, grayBrush, accentBrush;
    private nint back, next, cancel, browse, pathBox, appsBox, hiveBox, typesBox, folderBox, openBox, body, folderLabel, space, free, progress, status;
    private Page page;
    private bool installing;
    private int exitCode = 1;
    private string installedFolder = "";
    private bool addedPath, capturedInstalling;
    private volatile string progressText = "";
    private volatile Installer.Result? result;
    private (bool Msvc, bool Clang, bool Node)? needs;

    private Wizard(string? testOut) => this.testOut = testOut;

    /// <summary>Shows the wizard until it closes: 0 when Larva was installed. With <paramref name="testOut"/>, it walks
    /// the pages by itself, saves each as a PNG there and installs with no PATH or registry changes.</summary>
    public static int Run(string folder, bool addPath, bool register, bool hive, bool types, string? testOut)
    {
        var wizard = new Wizard(testOut);
        current = wizard;
        wizard.Create(folder, addPath, register, hive, types);
        if (testOut is not null)
            return wizard.Test();
        ShowWindow(wizard.hwnd, SW_SHOW);
        UpdateWindow(wizard.hwnd);
        MSG message;
        while (GetMessage(&message, 0, 0, 0) > 0)
        {
            if (IsDialogMessage(wizard.hwnd, &message) == 0)
            {
                TranslateMessage(&message);
                DispatchMessage(&message);
            }
        }
        return wizard.exitCode;
    }

    private int S(int value) => value * dpi / 96;

    private string Title => string.Format(t.Window, version);

    // ── The window ─────────────────────────────────────

    private void Create(string folder, bool addPath, bool register, bool hive, bool types)
    {
        var controls = new INITCOMMONCONTROLSEX { dwSize = (uint)sizeof(INITCOMMONCONTROLSEX), dwICC = 0x20 | 0x4000 };
        InitCommonControlsEx(&controls);
        instance = GetModuleHandle(0);
        whiteBrush = CreateSolidBrush(White);
        grayBrush = CreateSolidBrush(Gray);
        accentBrush = CreateSolidBrush(Accent);
        font = CreateFont(-S(12), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        bold = CreateFont(-S(15), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        big = CreateFont(-S(20), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
        // Larva's icon at the sizes the pages draw it (the .ico has them antialiased).
        bigIcon = LoadImage(instance, 32512, 1, S(96), S(96), 0);
        smallIcon = LoadImage(instance, 32512, 1, S(40), S(40), 0);

        nint icon = LoadIcon(instance, 32512);
        if (icon == 0)
            icon = LoadIcon(0, 32512);
        var windowClass = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            lpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nint, nint, nint>)&WndProc,
            hInstance = instance,
            hIcon = icon,
            hIconSm = icon,
            hCursor = LoadCursor(0, 32512),
            hbrBackground = whiteBrush,
            lpszClassName = Marshal.StringToHGlobalUni("LarvaSetupWizard"),
        };
        RegisterClassEx(&windowClass);

        const uint style = WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
        var frame = new RECT { right = S(Width), bottom = S(Height) };
        AdjustWindowRectEx(&frame, style, 0, WS_EX_CONTROLPARENT);
        int width = frame.right - frame.left, height = frame.bottom - frame.top;
        hwnd = CreateWindowEx(WS_EX_CONTROLPARENT, "LarvaSetupWizard", Title, style,
            (GetSystemMetrics(0) - width) / 2, (GetSystemMetrics(1) - height) / 2, width, height, 0, 0, instance, 0);

        back = Control("BUTTON", t.Back, WS_TABSTOP, IdBack, 300, 376, 88, 28);
        next = Control("BUTTON", t.Next, WS_VISIBLE | WS_TABSTOP | BS_DEFPUSHBUTTON, IdNext, 392, 376, 88, 28);
        cancel = Control("BUTTON", t.Cancel, WS_VISIBLE | WS_TABSTOP, IdCancel, 496, 376, 88, 28);
        body = Control("STATIC", "", SS_LEFT | SS_NOPREFIX | SS_EDITCONTROL, IdBody, 40, 90, 520, 250);
        folderLabel = Control("STATIC", t.Folder, SS_LEFT | SS_NOPREFIX, IdFolderLabel, 40, 94, 520, 20);
        folderBox = Control("EDIT", folder, WS_TABSTOP | ES_AUTOHSCROLL, IdFolder, 40, 116, 412, 25, WS_EX_CLIENTEDGE);
        browse = Control("BUTTON", t.Browse, WS_TABSTOP, IdBrowse, 460, 115, 100, 27);
        pathBox = Control("BUTTON", t.AddToPath, WS_TABSTOP | BS_AUTOCHECKBOX | BS_MULTILINE, IdPath, 40, 162, 520, 36);
        appsBox = Control("BUTTON", t.AddToApps, WS_TABSTOP | BS_AUTOCHECKBOX | BS_MULTILINE, IdApps, 40, 202, 520, 24);
        hiveBox = Control("BUTTON", t.AddHive, WS_TABSTOP | BS_AUTOCHECKBOX | BS_MULTILINE, IdHive, 40, 232, 520, 36);
        typesBox = Control("BUTTON", t.AddTypes, WS_TABSTOP | BS_AUTOCHECKBOX | BS_MULTILINE, IdTypes, 40, 272, 520, 24);
        space = Control("STATIC", string.Format(t.Space, Installer.Size(payloadSize, t.Decimal)), SS_LEFT | SS_NOPREFIX, IdSpace, 40, 310, 520, 20);
        free = Control("STATIC", "", SS_LEFT | SS_NOPREFIX, IdFree, 40, 330, 520, 20);
        progress = Control("msctls_progress32", null, 0, IdProgress, 40, 140, 520, 18);
        status = Control("STATIC", "", SS_LEFT | SS_NOPREFIX | SS_EDITCONTROL, IdStatus, 40, 166, 520, 40);
        openBox = Control("BUTTON", t.OpenFolder, WS_TABSTOP | BS_AUTOCHECKBOX, IdOpen, 40, 316, 520, 24);
        SendMessage(pathBox, BM_SETCHECK, addPath ? 1 : 0, 0);
        SendMessage(appsBox, BM_SETCHECK, register ? 1 : 0, 0);
        SendMessage(hiveBox, BM_SETCHECK, hive ? 1 : 0, 0);
        SendMessage(typesBox, BM_SETCHECK, types ? 1 : 0, 0);
        SendMessage(progress, PBM_SETRANGE32, 0, 1000);
        UpdateFree();
        ShowPage(Page.Welcome);
        SetFocus(next);
    }

    private nint Control(string kind, string? text, uint style, int id, int x, int y, int width, int height, uint extended = 0)
    {
        nint control = CreateWindowEx(extended, kind, text, WS_CHILD | style, S(x), S(y), S(width), S(height), hwnd, id, instance, 0);
        SendMessage(control, WM_SETFONT, font, 1);
        return control;
    }

    private static bool Checked(nint box) => SendMessage(box, BM_GETCHECK, 0, 0) == 1;

    private string FolderText()
    {
        char* buffer = stackalloc char[1024];
        int length = GetWindowText(folderBox, buffer, 1024);
        return new string(buffer, 0, length).Trim().Trim('"');
    }

    /// <summary>The space free where the folder box points, as the folder changes.</summary>
    private void UpdateFree() =>
        SetWindowText(free, Installer.FreeSpace(FolderText()) is { } bytes ? string.Format(t.Free, Installer.Size(bytes, t.Decimal)) : "");

    private void ShowPage(Page shown)
    {
        page = shown;
        static void Show(nint control, bool visible) => ShowWindow(control, visible ? SW_SHOW : SW_HIDE);
        bool options = shown == Page.Options;
        foreach (nint control in new[] { folderLabel, folderBox, browse, pathBox, appsBox, hiveBox, typesBox, space, free })
            Show(control, options);
        Show(progress, shown == Page.Installing);
        Show(status, shown == Page.Installing);
        Show(openBox, shown == Page.Done);
        Show(body, shown is Page.Welcome or Page.Ready or Page.Done);
        Show(back, shown is Page.Welcome or Page.Options or Page.Ready);
        EnableWindow(back, shown == Page.Welcome ? 0 : 1);
        EnableWindow(next, shown == Page.Installing ? 0 : 1);
        EnableWindow(cancel, shown is Page.Installing or Page.Done ? 0 : 1);
        SetWindowText(next, shown switch { Page.Ready => t.Install, Page.Done => t.Finish, _ => t.Next });

        switch (shown)
        {
            case Page.Welcome:
                MoveWindow(body, S(Panel + 24), S(104), S(Width - Panel - 48), S(Band - 112), 1);
                SetWindowText(body, string.Format(t.WelcomeBody, version));
                break;
            case Page.Ready:
                MoveWindow(body, S(40), S(86), S(Width - 80), S(Band - 92), 1);
                SetWindowText(body, ReadyText());
                break;
            case Page.Done:
                MoveWindow(body, S(40), S(92), S(Width - 80), S(210), 1);
                SetWindowText(body, string.Format(t.DoneBody, installedFolder, addedPath ? t.DoneOnPath : t.DoneNotOnPath));
                break;
        }
        InvalidateRect(hwnd, null, 1);
        if (shown != Page.Installing)
            SetFocus(next);
    }

    /// <summary>What Install will do: where, with which options, over what, and what the system has.</summary>
    private string ReadyText()
    {
        var text = new System.Text.StringBuilder();
        string folder = FolderText();
        text.Append(t.ReadyFolder).Append('\n').Append(folder).Append("\n\n");
        text.Append(t.ReadyOptions).Append('\n');
        bool path = Checked(pathBox), apps = Checked(appsBox), hive = Checked(hiveBox), types = Checked(typesBox);
        if (path)
            text.Append("      •  ").Append(t.ReadyPath).Append('\n');
        if (apps)
            text.Append("      •  ").Append(t.ReadyApps).Append('\n');
        if (hive)
            text.Append("      •  ").Append(t.ReadyHive).Append('\n');
        if (types)
            text.Append("      •  ").Append(t.ReadyTypes).Append('\n');
        if (!path && !apps && !hive && !types)
            text.Append("      ").Append(t.ReadyNoOptions).Append('\n');
        // What is there already: the registered installation (an upgrade), or some other larva in that folder.
        bool registered = previousVersion is not null && previousFolder is not null
            && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(previousFolder)), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase);
        if (registered)
        {
            text.Append('\n').Append(previousVersion == version
                ? string.Format(t.Reinstall, version)
                : string.Format(t.Upgrade, previousVersion, version)).Append('\n');
        }
        else if (File.Exists(Path.Combine(folder, "larva.exe")))
            text.Append('\n').Append(t.Replace).Append('\n');
        var found = needs ??= Installer.Prerequisites();
        text.Append('\n').Append(t.ReadyNeeds).Append('\n');
        void Need(bool ok, string yes, string no) => text.Append("      ").Append(ok ? "✔  " + yes : "✘  " + no).Append('\n');
        Need(found.Msvc, t.Msvc, t.MsvcMissing);
        Need(found.Clang, t.Clang, t.ClangMissing);
        Need(found.Node, t.Node, t.NodeMissing);
        if (!found.Msvc || !found.Clang || !found.Node)
            text.Append("      ").Append(t.ReadyNeedsNote).Append('\n');
        text.Append('\n').Append(t.ReadyGo);
        return text.ToString();
    }

    // ── Painting ───────────────────────────────────────

    private void Paint()
    {
        PAINTSTRUCT paint;
        nint dc = BeginPaint(hwnd, &paint);
        SetBkMode(dc, TRANSPARENT);
        var band = new RECT { top = S(Band), right = S(Width), bottom = S(Height) };
        FillRect(dc, &band, grayBrush);
        nint pen = CreatePen(0, 1, LineColor);
        nint oldPen = SelectObject(dc, pen);
        MoveToEx(dc, 0, S(Band), 0);
        LineTo(dc, S(Width), S(Band));
        nint oldFont = SelectObject(dc, font);
        // The brand, as installers sign their bottom band.
        SetTextColor(dc, Faint);
        Text(dc, string.Format(t.Brand, version), 16, 376, 200, 28, DT_SINGLELINE | DT_VCENTER);
        if (page == Page.Welcome)
        {
            var panel = new RECT { right = S(Panel), bottom = S(Band) };
            FillRect(dc, &panel, accentBrush);
            if (bigIcon != 0)
            {
                // The icon brings its own dark tile: it reads on the green panel as it is.
                DrawIconEx(dc, S(Panel / 2 - 48), S(112 - 48), bigIcon, S(96), S(96), 0, 0, 3);
            }
            else
                Logo(dc, S(Panel / 2), S(112), S(46), onAccent: true);
            SelectObject(dc, bold);
            SetTextColor(dc, White);
            Text(dc, "Larva", 0, 182, Panel, 24, DT_CENTER | DT_SINGLELINE);
            SelectObject(dc, font);
            SetTextColor(dc, Pale);
            Text(dc, version, 0, 206, Panel, 20, DT_CENTER | DT_SINGLELINE);
            SelectObject(dc, big);
            SetTextColor(dc, Black);
            Text(dc, string.Format(t.WelcomeTitle, version), Panel + 24, 22, Width - Panel - 48, 76, DT_WORDBREAK);
        }
        else
        {
            MoveToEx(dc, 0, S(Header), 0);
            LineTo(dc, S(Width), S(Header));
            (string title, string subtitle) = page switch
            {
                Page.Options => (t.OptionsTitle, t.OptionsSubtitle),
                Page.Ready => (t.ReadyTitle, t.ReadySubtitle),
                Page.Installing => (t.InstallingTitle, string.Format(t.InstallingSubtitle, version)),
                _ => (t.DoneTitle, string.Format(t.DoneSubtitle, version)),
            };
            SelectObject(dc, bold);
            SetTextColor(dc, Black);
            Text(dc, title, 24, 14, 480, 24, DT_SINGLELINE);
            SelectObject(dc, font);
            SetTextColor(dc, Dim);
            Text(dc, subtitle, 40, 40, 470, 20, DT_SINGLELINE);
            if (smallIcon != 0)
                DrawIconEx(dc, S(Width - 60), S((Header - 40) / 2), smallIcon, S(40), S(40), 0, 0, 3);
            else
                Logo(dc, S(Width - 44), S(Header / 2), S(22), onAccent: false);
        }
        SelectObject(dc, oldFont);
        SelectObject(dc, oldPen);
        DeleteObject(pen);
        EndPaint(hwnd, &paint);
    }

    private void Text(nint dc, string text, int x, int y, int width, int height, int format)
    {
        var box = new RECT { left = S(x), top = S(y), right = S(x + width), bottom = S(y + height) };
        DrawText(dc, text, text.Length, &box, (uint)(format | DT_NOPREFIX));
    }

    /// <summary>Larva's mark drawn by hand (when the icon is missing): a worm of round segments in a circle.</summary>
    private void Logo(nint dc, int x, int y, int radius, bool onAccent)
    {
        nint oldPen = SelectObject(dc, GetStockObject(8)); // NULL_PEN
        nint oldBrush = SelectObject(dc, onAccent ? whiteBrush : accentBrush);
        Ellipse(dc, x - radius, y - radius, x + radius, y + radius);
        SelectObject(dc, onAccent ? accentBrush : whiteBrush);
        foreach (var (u, v, r) in Segments)
        {
            int cx = x + (int)(u * radius), cy = y + (int)(v * radius), cr = (int)(r * radius);
            Ellipse(dc, cx - cr, cy - cr, cx + cr, cy + cr);
        }
        SelectObject(dc, oldBrush);
        SelectObject(dc, oldPen);
    }

    /// <summary>The segments, centers and radius as fractions of the circle's radius: a worm that thickens to its head.</summary>
    private static readonly (double U, double V, double R)[] Segments =
        [(-0.62, 0.18, 0.12), (-0.40, 0.04, 0.16), (-0.14, 0.12, 0.19), (0.14, 0.12, 0.21), (0.44, -0.08, 0.25)];

    // ── Actions ────────────────────────────────────────

    private void Next()
    {
        switch (page)
        {
            case Page.Welcome:
                ShowPage(Page.Options);
                break;
            case Page.Options:
                string folder = FolderText();
                try
                {
                    folder = folder.Length > 0 && Path.IsPathFullyQualified(folder) ? Path.GetFullPath(folder) : "";
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    folder = "";
                }
                if (folder.Length == 0)
                {
                    MessageBox(hwnd, t.FolderEmpty, Title, MB_ICONINFORMATION);
                    SetFocus(folderBox);
                    return;
                }
                SetWindowText(folderBox, folder);
                ShowPage(Page.Ready);
                break;
            case Page.Ready:
                StartInstall();
                break;
            case Page.Done:
                Finish();
                break;
        }
    }

    private void StartInstall()
    {
        string folder = FolderText();
        // larva running from there (an editor's server, a build) holds its files.
        for (int running; (running = Installer.RunningFrom(folder)) > 0;)
        {
            if (testOut is not null || MessageBox(hwnd, string.Format(t.Running, running), Title, MB_RETRYCANCEL | MB_ICONINFORMATION) != IDRETRY)
                return;
        }
        bool path = Checked(pathBox), register = Checked(appsBox), hive = Checked(hiveBox), types = Checked(typesBox);
        if (testOut is not null)
            path = register = hive = types = false; // a test never touches the user's PATH, Settings > Apps, file types or larva's settings
        installing = true;
        installedFolder = folder;
        addedPath = path;
        SendMessage(progress, PBM_SETPOS, 0, 0);
        SetWindowText(status, "");
        ShowPage(Page.Installing);
        nint window = hwnd;
        new Thread(() =>
        {
            var outcome = Installer.Install(folder, path, register, hive, types, (fraction, what) =>
            {
                progressText = what;
                PostMessage(window, WM_PROGRESS, (nint)(fraction * 1000), 0);
            });
            result = outcome;
            PostMessage(window, WM_INSTALLED, 0, 0);
        }) { IsBackground = true }.Start();
    }

    private void Installed()
    {
        installing = false;
        var outcome = result!;
        if (outcome.Success)
        {
            exitCode = 0;
            SendMessage(progress, PBM_SETPOS, 1000, 0);
            ShowPage(Page.Done);
            return;
        }
        if (testOut is not null)
            File.WriteAllText(Path.Combine(testOut, "error.txt"), outcome.Error);
        else
            MessageBox(hwnd, string.Format(t.Failed, outcome.Error), Title, MB_ICONERROR);
        ShowPage(Page.Ready);
    }

    private void Cancel()
    {
        if (installing)
            return;
        if (page == Page.Done)
        {
            Finish();
            return;
        }
        if (MessageBox(hwnd, t.ConfirmCancel, Title, MB_YESNO | MB_ICONQUESTION) == IDYES)
            DestroyWindow(hwnd);
    }

    private void Finish()
    {
        if (Checked(openBox))
            ShellExecute(hwnd, "open", installedFolder, null, null, SW_SHOWNORMAL);
        DestroyWindow(hwnd);
    }

    private void Browse()
    {
        char* name = stackalloc char[260];
        nint title = Marshal.StringToHGlobalUni(t.BrowseTitle);
        var info = new BROWSEINFOW { hwndOwner = hwnd, pszDisplayName = (nint)name, lpszTitle = title, ulFlags = 0x1 | 0x40 };
        nint chosen = SHBrowseForFolder(&info);
        Marshal.FreeHGlobal(title);
        if (chosen == 0)
            return;
        char* buffer = stackalloc char[1024];
        if (SHGetPathFromIDList(chosen, buffer) != 0)
        {
            string folder = new(buffer);
            // A folder picked to hold Larva gets its own Larva folder inside.
            if (!string.Equals(Path.GetFileName(folder.TrimEnd('\\')), Installer.Product, StringComparison.OrdinalIgnoreCase))
                folder = Path.Combine(folder, Installer.Product);
            SetWindowText(folderBox, folder);
        }
        CoTaskMemFree(chosen);
    }

    // ── Messages ───────────────────────────────────────

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (current is { } wizard && wizard.hwnd == hwnd && wizard.Handle(message, wParam, lParam) is { } handled)
                return handled;
        }
        catch (Exception e)
        {
            MessageBox(hwnd, e.ToString(), "LarvaSetup", MB_ICONERROR);
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private nint? Handle(uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WM_PAINT:
                Paint();
                return 0;
            case WM_CTLCOLORSTATIC:
                SetBkMode(wParam, TRANSPARENT);
                SetTextColor(wParam, lParam == space || lParam == free ? Dim : Black);
                return whiteBrush;
            case WM_COMMAND when (int)(wParam & 0xFFFF) == IdFolder && (wParam >> 16 & 0xFFFF) == EN_CHANGE:
                UpdateFree();
                return 0;
            case WM_COMMAND when (wParam >> 16 & 0xFFFF) == 0:
                switch ((int)(wParam & 0xFFFF))
                {
                    case IdNext: Next(); break;
                    case IdBack when page is Page.Options or Page.Ready: ShowPage(page - 1); break;
                    case IdCancel: Cancel(); break;
                    case IdBrowse: Browse(); break;
                    default: return null;
                }
                return 0;
            case WM_PROGRESS:
                SendMessage(progress, PBM_SETPOS, wParam, 0);
                if (progressText.Length > 0)
                {
                    SetWindowText(status, progressText switch
                    {
                        Installer.StepPath => t.StepPath,
                        Installer.StepApps => t.StepApps,
                        Installer.StepHive => t.StepHive,
                        Installer.StepTypes => t.StepTypes,
                        Installer.StepCheck => t.StepCheck,
                        var file => string.Format(t.Copying, file),
                    });
                }
                if (testOut is not null && !capturedInstalling && wParam >= 300)
                {
                    capturedInstalling = true;
                    UpdateWindow(hwnd); // not Pump: that would also handle the end of the installation
                    Capture("4-installing");
                }
                return 0;
            case WM_INSTALLED:
                Installed();
                return 0;
            case WM_CLOSE:
                Cancel();
                return 0;
            case WM_DESTROY:
                PostQuitMessage(exitCode);
                return 0;
        }
        return null;
    }

    // ── Test mode: every page as a PNG ─────────────────

    private int Test()
    {
        Directory.CreateDirectory(testOut!);
        ShowWindow(hwnd, SW_SHOW);
        SetForegroundWindow(hwnd);
        Pump();
        Capture("1-welcome");
        Next();
        Pump();
        Capture("2-options");
        Next();
        Pump();
        Capture("3-ready");
        Next();
        while (installing)
        {
            Pump();
            Thread.Sleep(10);
        }
        Pump();
        Capture(result is { Success: true } ? "5-done" : "5-failed");
        int code = exitCode;
        DestroyWindow(hwnd);
        Pump();
        return code;
    }

    /// <summary>Handles what is waiting, for a moment (the window paints).</summary>
    private static void Pump()
    {
        MSG message;
        for (int round = 0; round < 10; round++)
        {
            while (PeekMessage(&message, 0, 0, 0, 1) != 0)
            {
                TranslateMessage(&message);
                DispatchMessage(&message);
            }
            Thread.Sleep(15);
        }
    }

    private void Capture(string name)
    {
        RECT frame;
        GetWindowRect(hwnd, &frame);
        int width = frame.right - frame.left, height = frame.bottom - frame.top;
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen);
        var header = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };
        void* bits;
        nint bitmap = CreateDIBSection(screen, &header, 0, &bits, 0, 0);
        nint old = SelectObject(memory, bitmap);
        PrintWindow(hwnd, memory, 2); // PW_RENDERFULLCONTENT
        Png.Write(Path.Combine(testOut!, name + ".png"), (byte*)bits, width, height);
        SelectObject(memory, old);
        DeleteObject(bitmap);
        DeleteDC(memory);
        ReleaseDC(0, screen);
    }

    private static class Png
    {
        private static readonly uint[] Crcs = Enumerable.Range(0, 256).Select(n =>
        {
            uint c = (uint)n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();

        public static void Write(string file, byte* bgra, int width, int height)
        {
            using var output = File.Create(file);
            output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
            var header = new byte[13];
            BinaryPrimitives.WriteInt32BigEndian(header, width);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
            header[8] = 8; // bits per sample
            header[9] = 2; // RGB
            Chunk(output, "IHDR", header);
            var pixels = new MemoryStream();
            using (var z = new ZLibStream(pixels, CompressionLevel.Fastest, leaveOpen: true))
            {
                var row = new byte[1 + width * 3];
                for (int y = 0; y < height; y++)
                {
                    byte* source = bgra + (long)y * width * 4;
                    for (int x = 0; x < width; x++)
                    {
                        row[1 + x * 3] = source[x * 4 + 2];
                        row[2 + x * 3] = source[x * 4 + 1];
                        row[3 + x * 3] = source[x * 4];
                    }
                    z.Write(row);
                }
            }
            Chunk(output, "IDAT", pixels.ToArray());
            Chunk(output, "IEND", []);
        }

        private static void Chunk(Stream output, string type, byte[] data)
        {
            Span<byte> number = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
            output.Write(number);
            byte[] name = System.Text.Encoding.ASCII.GetBytes(type);
            output.Write(name);
            output.Write(data);
            uint crc = 0xFFFFFFFF;
            foreach (byte b in name.Concat(data))
                crc = Crcs[(crc ^ b) & 0xFF] ^ (crc >> 8);
            BinaryPrimitives.WriteUInt32BigEndian(number, crc ^ 0xFFFFFFFF);
            output.Write(number);
        }
    }
}
