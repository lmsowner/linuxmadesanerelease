// Copyright (c) Richard D. Kiernan.
// Licensed under the Business Source License 1.1. See LICENSE for details.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using LinuxMadeSane.Core.Models.DesktopSession;

namespace LinuxMadeSane.DesktopHelper;

internal sealed class DesktopAssistantWindow : Window
{
    private readonly DesktopAssistantAvaloniaContext context;
    private readonly StackPanel sessionPanel = new() { Spacing = 6 };
    private readonly StackPanel messagePanel = new() { Spacing = 10 };
    private readonly ScrollViewer messageScroll = new();
    private readonly TextBlock statusText = new();
    private readonly TextBlock errorText = new();
    private readonly TextBlock sidebarTitle = new()
    {
        Text = "Desktop Assistant",
        FontSize = 18,
        FontWeight = FontWeight.SemiBold
    };
    private readonly ComboBox providerBox = new() { MinWidth = 170 };
    private readonly ComboBox modelBox = new() { MinWidth = 180 };
    private readonly TextBlock headerTitle = new()
    {
        Text = "Talk to your desktop",
        FontSize = 22,
        FontWeight = FontWeight.SemiBold
    };
    private readonly TextBlock headerSubtitle = new()
    {
        Text = "Diagnose, Deep Fix, approve changes, and keep the chat history."
    };
    private readonly TextBox promptBox = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 64,
        MaxHeight = 130
    };
    private readonly Button sendButton = new() { Content = "Ask AI" };
    private readonly Button diagnoseButton = new() { Content = "Diagnose" };
    private readonly Button deepFixButton = new() { Content = "Deep Fix" };
    private readonly Border approvalPanel = new()
    {
        IsVisible = false,
        Padding = new Thickness(10),
        CornerRadius = new CornerRadius(8)
    };
    private readonly TextBlock approvalTitle = new() { FontWeight = FontWeight.SemiBold };
    private readonly TextBlock approvalDescription = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<Button> primaryButtons = [];
    private readonly List<Button> secondaryButtons = [];
    private Border? sidebarBorder;

    private DesktopAssistantNativeWorkspaceResponse? workspace;
    private DesktopAssistantNativeProposedFix? pendingFix;
    private DesktopAssistantTheme theme = DesktopAssistantTheme.From(null);
    private Guid? activeSessionId;
    private Guid? sessionPendingDeleteId;
    private string selectedProviderKey = string.Empty;
    private string selectedModelId = string.Empty;
    private bool suppressSelectorEvents;
    private bool busy;

    public DesktopAssistantWindow(DesktopAssistantAvaloniaContext context)
    {
        this.context = context;
        Title = "Linux Made Sane Desktop Assistant";
        Width = 1040;
        Height = 720;
        MinWidth = 760;
        MinHeight = 520;
        Icon = context.WindowIcon;
        Background = theme.Brush(theme.Base);
        Content = BuildLayout();
        ApplyTheme();

        Closing += (_, args) =>
        {
            args.Cancel = true;
            Hide();
        };
        context.NativeMessageBus.ThemeChanged += HandleThemeChanged;
        Opened += async (_, _) => await RefreshWorkspaceAsync();
    }

    private Control BuildLayout()
    {
        var root = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("230,*"),
            RowDefinitions = new RowDefinitions("*")
        };

        var sidebar = BuildSidebar();
        Grid.SetColumn(sidebar, 0);
        root.Children.Add(sidebar);

        var main = BuildMainPanel();
        Grid.SetColumn(main, 1);
        root.Children.Add(main);

        return root;
    }

    private Control BuildSidebar()
    {
        var newButton = new Button
        {
            Content = "New chat",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        secondaryButtons.Add(newButton);
        newButton.Click += async (_, _) => await CreateSessionAsync();

        var openLmsButton = new Button
        {
            Content = "Open LMS",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        secondaryButtons.Add(openLmsButton);
        openLmsButton.Click += (_, _) => context.OpenLocalLms("/desktop-assistant?fromTray=1");

        sidebarBorder = new Border
        {
            Padding = new Thickness(14),
            Background = theme.Brush(theme.PanelStrong),
            Child = new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    DockTop(new StackPanel
                    {
                        Spacing = 8,
                        Children =
                        {
                            sidebarTitle,
                            statusText,
                            newButton,
                            openLmsButton
                        }
                    }),
                    new ScrollViewer
                    {
                        Content = sessionPanel,
                        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
                    }
                }
            }
        };
        return sidebarBorder;
    }

    private Control BuildMainPanel()
    {
        errorText.Foreground = new SolidColorBrush(Color.Parse("#ffb4ab"));
        errorText.TextWrapping = TextWrapping.Wrap;
        errorText.IsVisible = false;
        primaryButtons.Add(sendButton);
        secondaryButtons.Add(diagnoseButton);
        secondaryButtons.Add(deepFixButton);

        providerBox.SelectionChanged += (_, _) =>
        {
            if (suppressSelectorEvents)
            {
                return;
            }

            selectedProviderKey = (providerBox.SelectedItem as DesktopAssistantOption)?.Key ?? string.Empty;
            PopulateModelSelector();
        };
        modelBox.SelectionChanged += (_, _) =>
        {
            if (!suppressSelectorEvents)
            {
                selectedModelId = (modelBox.SelectedItem as DesktopAssistantOption)?.Key ?? string.Empty;
            }
        };

        messageScroll.Content = messagePanel;
        messageScroll.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;

        promptBox.PlaceholderText = "Ask LMS to fix the desktop...";
        promptBox.AddHandler(InputElement.KeyDownEvent, HandlePromptKeyDown, RoutingStrategies.Tunnel);

        sendButton.Click += async (_, _) => await SendPromptAsync();
        diagnoseButton.Click += async (_, _) => await SendPresetAsync("Diagnose this graphical desktop from the LMS session agent evidence. Start with the most likely issue areas, then give the single best next LMS action. Keep it short and do not ask me to copy commands.");
        deepFixButton.Click += async (_, _) => await SendDeepFixAsync();

        var approvalYes = new Button { Content = "Yes, fix it" };
        primaryButtons.Add(approvalYes);
        approvalYes.Click += async (_, _) => await ApproveFixAsync();
        var approvalNo = new Button { Content = "No" };
        secondaryButtons.Add(approvalNo);
        approvalNo.Click += (_, _) =>
        {
            pendingFix = null;
            approvalPanel.IsVisible = false;
        };
        approvalPanel.Child = new DockPanel
        {
            LastChildFill = true,
            Children =
            {
                DockRight(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { approvalYes, approvalNo }
                }),
                new StackPanel
                {
                    Spacing = 4,
                    Children = { approvalTitle, approvalDescription }
                }
            }
        };

        return new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"),
            Children =
            {
                Row(0, BuildHeader()),
                Row(1, messageScroll),
                Row(2, errorText),
                Row(3, approvalPanel),
                Row(4, BuildComposer())
            }
        };
    }

    private Control BuildHeader()
    {
        return new Border
        {
            Padding = new Thickness(0, 0, 0, 12),
            Child = new DockPanel
            {
                LastChildFill = false,
                Children =
                {
                    DockLeft(new StackPanel
                    {
                        Children =
                        {
                            headerTitle,
                            headerSubtitle
                        }
                    }),
                    DockRight(new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            providerBox,
                            modelBox
                        }
                    })
                }
            }
        };
    }

    private Control BuildComposer()
    {
        return new Border
        {
            Padding = new Thickness(0, 10, 0, 0),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    Column(0, promptBox),
                    Column(1, new StackPanel
                    {
                        Margin = new Thickness(10, 0, 0, 0),
                        Spacing = 8,
                        Width = 104,
                        Children =
                        {
                            sendButton,
                            diagnoseButton,
                            deepFixButton
                        }
                    })
                }
            }
        };
    }

    private async Task RefreshWorkspaceAsync()
    {
        await RunBusyAsync(async token =>
        {
            workspace = await context.ApiClient.GetWorkspaceAsync(activeSessionId, token);
            activeSessionId = workspace.ActiveSessionId;
            pendingFix = workspace.ProposedFix;
            SyncSelectorsFromWorkspace();
            RenderWorkspace();
        });
    }

    private async Task CreateSessionAsync()
    {
        await RunBusyAsync(async token =>
        {
            workspace = await context.ApiClient.CreateSessionAsync(selectedProviderKey, selectedModelId, token);
            activeSessionId = workspace.ActiveSessionId;
            pendingFix = null;
            sessionPendingDeleteId = null;
            SyncSelectorsFromWorkspace();
            RenderWorkspace();
        });
    }

    private async Task DeleteSessionAsync(Guid sessionId)
    {
        await RunBusyAsync(async token =>
        {
            workspace = await context.ApiClient.DeleteSessionAsync(sessionId, token);
            activeSessionId = workspace.ActiveSessionId;
            pendingFix = workspace.ProposedFix;
            sessionPendingDeleteId = null;
            SyncSelectorsFromWorkspace();
            RenderWorkspace();
        });
    }

    private async Task SendPromptAsync()
    {
        if (busy)
        {
            return;
        }

        var prompt = promptBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        promptBox.Text = string.Empty;
        await SendMessageAsync(prompt);
    }

    private async void HandlePromptKeyDown(object? sender, KeyEventArgs args)
    {
        if (!IsPromptSubmitKey(args.Key) || args.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        args.Handled = true;
        await SendPromptAsync();
    }

    private static bool IsPromptSubmitKey(Key key) =>
        key == Key.Enter;

    private Task SendPresetAsync(string prompt) => SendMessageAsync(prompt);

    private Task SendDeepFixAsync()
    {
        var issue = promptBox.Text?.Trim() ?? string.Empty;
        var prompt = string.IsNullOrWhiteSpace(issue)
            ? "Deep Fix this desktop session. Use LMS-collected evidence first. Give the single next LMS fix or approval needed. Keep it short and do not give command-copy instructions."
            : $"""
Deep Fix this desktop problem:
{issue}

Use LMS-collected helper diagnostics and desktop evidence first. Give the single next LMS fix or approval needed. Keep it short and do not give command-copy instructions.
""";

        promptBox.Text = string.Empty;
        return SendMessageAsync(prompt);
    }

    private async Task SendMessageAsync(string prompt)
    {
        await RunBusyAsync(async token =>
        {
            pendingFix = null;
            workspace = await context.ApiClient.SendMessageAsync(
                activeSessionId,
                prompt,
                selectedProviderKey,
                selectedModelId,
                token);
            activeSessionId = workspace.ActiveSessionId;
            pendingFix = workspace.ProposedFix;
            SyncSelectorsFromWorkspace();
            RenderWorkspace();
        });
    }

    private async Task ApproveFixAsync()
    {
        if (pendingFix is null)
        {
            return;
        }

        await RunBusyAsync(async token =>
        {
            workspace = await context.ApiClient.ApproveFixAsync(
                activeSessionId,
                pendingFix,
                selectedProviderKey,
                selectedModelId,
                token);
            activeSessionId = workspace.ActiveSessionId;
            pendingFix = workspace.ProposedFix;
            SyncSelectorsFromWorkspace();
            RenderWorkspace();
        });
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> action)
    {
        if (busy)
        {
            return;
        }

        busy = true;
        SetBusyState();
        errorText.IsVisible = false;
        errorText.Text = string.Empty;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await action(timeout.Token);
        }
        catch (Exception exception)
        {
            errorText.Text = exception.Message;
            errorText.IsVisible = true;
        }
        finally
        {
            busy = false;
            SetBusyState();
        }
    }

    private void SetBusyState()
    {
        sendButton.IsEnabled = !busy;
        diagnoseButton.IsEnabled = !busy;
        deepFixButton.IsEnabled = !busy;
        providerBox.IsEnabled = !busy;
        modelBox.IsEnabled = !busy;
    }

    private void SyncSelectorsFromWorkspace()
    {
        if (workspace is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(workspace.ActiveProviderKey))
        {
            selectedProviderKey = workspace.ActiveProviderKey;
        }

        if (!string.IsNullOrWhiteSpace(workspace.ModelId))
        {
            selectedModelId = workspace.ModelId;
        }

        PopulateProviderSelector();
        PopulateModelSelector();
    }

    private void PopulateProviderSelector()
    {
        if (workspace is null)
        {
            return;
        }

        suppressSelectorEvents = true;
        var options = workspace.Providers
            .Select(provider => new DesktopAssistantOption(provider.ProviderKey, provider.DisplayName))
            .ToArray();
        providerBox.ItemsSource = options;
        providerBox.SelectedItem = options.FirstOrDefault(option =>
            option.Key.Equals(selectedProviderKey, StringComparison.OrdinalIgnoreCase)) ??
            options.FirstOrDefault();
        selectedProviderKey = (providerBox.SelectedItem as DesktopAssistantOption)?.Key ?? selectedProviderKey;
        suppressSelectorEvents = false;
    }

    private void PopulateModelSelector()
    {
        if (workspace is null)
        {
            return;
        }

        suppressSelectorEvents = true;
        var options = workspace.Models
            .Where(model => model.ProviderKey.Equals(selectedProviderKey, StringComparison.OrdinalIgnoreCase))
            .Select(model => new DesktopAssistantOption(model.ModelId, model.DisplayName))
            .ToArray();
        modelBox.ItemsSource = options;
        modelBox.SelectedItem = options.FirstOrDefault(option =>
            option.Key.Equals(selectedModelId, StringComparison.OrdinalIgnoreCase)) ??
            options.FirstOrDefault();
        selectedModelId = (modelBox.SelectedItem as DesktopAssistantOption)?.Key ?? selectedModelId;
        suppressSelectorEvents = false;
    }

    private void RenderWorkspace()
    {
        if (workspace is null)
        {
            return;
        }

        theme = DesktopAssistantTheme.From(workspace.Theme);
        ApplyTheme();
        statusText.Text = workspace.StatusSummary;
        RenderSessions();
        RenderMessages();
        RenderApproval();
    }

    private void HandleThemeChanged(DesktopAssistantNativeTheme nativeTheme) =>
        Dispatcher.UIThread.Post(() => ApplyNativeTheme(nativeTheme));

    private void ApplyNativeTheme(DesktopAssistantNativeTheme nativeTheme)
    {
        theme = DesktopAssistantTheme.From(nativeTheme);
        if (workspace is not null)
        {
            workspace = workspace with { Theme = nativeTheme };
        }

        ApplyTheme();
        RenderSessions();
        RenderMessages();
        RenderApproval();
    }

    private void ApplyTheme()
    {
        RequestedThemeVariant = IsDark(theme.Base) ? ThemeVariant.Dark : ThemeVariant.Light;
        Background = theme.Brush(theme.Base);
        if (sidebarBorder is not null)
        {
            sidebarBorder.Background = theme.Brush(theme.PanelStrong);
        }

        statusText.Foreground = theme.Brush(theme.Muted);
        sidebarTitle.Foreground = theme.Brush(theme.Text);
        headerTitle.Foreground = theme.Brush(theme.Text);
        headerSubtitle.Foreground = theme.Brush(theme.Muted);
        errorText.Foreground = theme.Brush(theme.Danger);
        approvalPanel.Background = theme.Brush(theme.Approval, 0.92);
        approvalTitle.Foreground = theme.Brush(theme.Warning);
        approvalDescription.Foreground = theme.Brush(theme.Text);
        ApplyTextBoxTheme(promptBox);
        ApplyComboBoxTheme(providerBox);
        ApplyComboBoxTheme(modelBox);

        foreach (var button in primaryButtons)
        {
            button.Background = theme.Brush(theme.Accent);
            button.Foreground = theme.Brush(theme.OnAccent);
            button.BorderBrush = theme.Brush(theme.AccentBright, 0.42);
        }

        foreach (var button in secondaryButtons)
        {
            button.Background = theme.Brush(theme.PanelSoft, 0.86);
            button.Foreground = theme.Brush(theme.Text);
            button.BorderBrush = theme.Brush(theme.Line);
        }
    }

    private void ApplyTextBoxTheme(TextBox textBox)
    {
        var background = theme.Brush(theme.PanelSoft);
        var focusedBackground = theme.Brush(theme.PanelStrong);
        var foreground = theme.Brush(theme.Text);
        var placeholder = theme.Brush(theme.Muted, 0.92);
        var border = theme.Brush(theme.Line);
        var focusedBorder = theme.Brush(theme.AccentBright, 0.88);

        textBox.Background = background;
        textBox.Foreground = foreground;
        textBox.PlaceholderForeground = placeholder;
        textBox.BorderBrush = border;
        textBox.CaretBrush = foreground;
        textBox.SelectionBrush = theme.Brush(theme.AccentBright, 0.42);
        textBox.SelectionForegroundBrush = foreground;
        textBox.Resources["TextControlBackground"] = background;
        textBox.Resources["TextControlBackgroundPointerOver"] = focusedBackground;
        textBox.Resources["TextControlBackgroundFocused"] = focusedBackground;
        textBox.Resources["TextControlForeground"] = foreground;
        textBox.Resources["TextControlForegroundPointerOver"] = foreground;
        textBox.Resources["TextControlForegroundFocused"] = foreground;
        textBox.Resources["TextControlPlaceholderForeground"] = placeholder;
        textBox.Resources["TextControlPlaceholderForegroundPointerOver"] = placeholder;
        textBox.Resources["TextControlPlaceholderForegroundFocused"] = placeholder;
        textBox.Resources["TextControlBorderBrush"] = border;
        textBox.Resources["TextControlBorderBrushPointerOver"] = focusedBorder;
        textBox.Resources["TextControlBorderBrushFocused"] = focusedBorder;
    }

    private void ApplyComboBoxTheme(ComboBox comboBox)
    {
        var background = theme.Brush(theme.PanelSoft);
        var focusedBackground = theme.Brush(theme.PanelStrong);
        var foreground = theme.Brush(theme.Text);
        var border = theme.Brush(theme.Line);
        var focusedBorder = theme.Brush(theme.AccentBright, 0.72);

        comboBox.Background = background;
        comboBox.Foreground = foreground;
        comboBox.PlaceholderForeground = theme.Brush(theme.Muted, 0.92);
        comboBox.BorderBrush = border;
        comboBox.Resources["ComboBoxBackground"] = background;
        comboBox.Resources["ComboBoxBackgroundPointerOver"] = focusedBackground;
        comboBox.Resources["ComboBoxBackgroundFocused"] = focusedBackground;
        comboBox.Resources["ComboBoxForeground"] = foreground;
        comboBox.Resources["ComboBoxForegroundPointerOver"] = foreground;
        comboBox.Resources["ComboBoxForegroundFocused"] = foreground;
        comboBox.Resources["ComboBoxBorderBrush"] = border;
        comboBox.Resources["ComboBoxBorderBrushPointerOver"] = focusedBorder;
        comboBox.Resources["ComboBoxBorderBrushFocused"] = focusedBorder;
    }

    private void RenderSessions()
    {
        sessionPanel.Children.Clear();
        if (workspace is null || workspace.Sessions.Count == 0)
        {
            sessionPendingDeleteId = null;
            sessionPanel.Children.Add(new TextBlock
            {
                Text = "No saved chats yet.",
                Foreground = theme.Brush(theme.Muted),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        if (sessionPendingDeleteId.HasValue &&
            workspace.Sessions.All(session => session.Id != sessionPendingDeleteId.Value))
        {
            sessionPendingDeleteId = null;
        }

        foreach (var session in workspace.Sessions)
        {
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8),
                Background = theme.Brush(session.Id == activeSessionId ? theme.PanelSoft : theme.PanelStrong, 0.78),
                BorderBrush = theme.Brush(session.Id == activeSessionId ? theme.Accent : theme.Line, 0.64),
                BorderThickness = new Thickness(1),
                Foreground = theme.Brush(theme.Text),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = session.Title,
                            FontWeight = session.Id == activeSessionId ? FontWeight.SemiBold : FontWeight.Normal,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = $"{session.MessageCount} messages",
                            FontSize = 11,
                            Foreground = theme.Brush(theme.Muted)
                        }
                    }
                }
            };
            var sessionId = session.Id;
            button.Click += async (_, _) =>
            {
                if (busy)
                {
                    return;
                }

                sessionPendingDeleteId = null;
                activeSessionId = sessionId;
                await RefreshWorkspaceAsync();
            };

            var deleteButton = new Button
            {
                Content = sessionPendingDeleteId == sessionId ? "Confirm" : "Delete",
                MinWidth = 68,
                Margin = new Thickness(6, 0, 0, 0),
                Padding = new Thickness(8),
                Background = theme.Brush(sessionPendingDeleteId == sessionId ? theme.Danger : theme.PanelStrong, 0.88),
                BorderBrush = theme.Brush(sessionPendingDeleteId == sessionId ? theme.Danger : theme.Line, 0.72),
                BorderThickness = new Thickness(1),
                Foreground = theme.Brush(sessionPendingDeleteId == sessionId ? theme.OnAccent : theme.Muted)
            };
            deleteButton.Click += async (_, _) =>
            {
                if (busy)
                {
                    return;
                }

                if (sessionPendingDeleteId != sessionId)
                {
                    sessionPendingDeleteId = sessionId;
                    RenderSessions();
                    return;
                }

                await DeleteSessionAsync(sessionId);
            };

            sessionPanel.Children.Add(new DockPanel
            {
                LastChildFill = true,
                Children =
                {
                    DockRight(deleteButton),
                    button
                }
            });
        }
    }

    private void RenderMessages()
    {
        messagePanel.Children.Clear();
        if (workspace is null || workspace.Messages.Count == 0)
        {
            messagePanel.Children.Add(new Border
            {
                Padding = new Thickness(14),
                CornerRadius = new CornerRadius(10),
                Background = theme.Brush(theme.AssistantBubble, 0.88),
                Child = new TextBlock
                {
                    Text = "Tell LMS what is wrong with the desktop.",
                    Foreground = theme.Brush(theme.Text)
                }
            });
            return;
        }

        foreach (var message in workspace.Messages)
        {
            var isUser = message.Role.Equals("user", StringComparison.OrdinalIgnoreCase);
            messagePanel.Children.Add(new Border
            {
                HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
                MaxWidth = isUser ? 560 : double.PositiveInfinity,
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(10),
                BorderBrush = theme.Brush(isUser ? theme.AccentBright : theme.Line, 0.34),
                BorderThickness = new Thickness(1),
                Background = theme.Brush(isUser ? theme.UserBubble : theme.AssistantBubble, isUser ? 0.96 : 0.9),
                Child = new StackPanel
                {
                    Spacing = 5,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = isUser ? "You" : "LMS",
                            FontSize = 11,
                            Foreground = theme.Brush(theme.Muted)
                        },
                        new TextBlock
                        {
                            Text = message.Content,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = theme.Brush(theme.Text)
                        }
                    }
                }
            });
        }

        Dispatcher.UIThread.Post(() => messageScroll.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void RenderApproval()
    {
        if (pendingFix is null)
        {
            approvalPanel.IsVisible = false;
            return;
        }

        approvalTitle.Text = pendingFix.Title;
        approvalDescription.Text = pendingFix.Description;
        approvalPanel.IsVisible = true;
    }

    private static Control DockTop(Control control)
    {
        DockPanel.SetDock(control, Dock.Top);
        return control;
    }

    private static Control DockLeft(Control control)
    {
        DockPanel.SetDock(control, Dock.Left);
        return control;
    }

    private static Control DockRight(Control control)
    {
        DockPanel.SetDock(control, Dock.Right);
        return control;
    }

    private static Control Row(int row, Control control)
    {
        Grid.SetRow(control, row);
        return control;
    }

    private static Control Column(int column, Control control)
    {
        Grid.SetColumn(control, column);
        return control;
    }

    private sealed record DesktopAssistantOption(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    private static bool IsDark(Color color)
    {
        static double Convert(byte channel)
        {
            var normalized = channel / 255d;
            return normalized <= 0.03928
                ? normalized / 12.92
                : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        var luminance = Convert(color.R) * 0.2126 + Convert(color.G) * 0.7152 + Convert(color.B) * 0.0722;
        return luminance < 0.45;
    }
}
