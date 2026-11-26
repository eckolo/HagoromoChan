using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using HagoromoChan.Contracts;

namespace HagoromoChan.Desktop;

// M05's explicit registration and plain-text inspection surface is independent of the Dot view.
internal sealed class CloudImportsWindow : Window
{
    private readonly RelayClient relay;
    private readonly CancellationToken lifetime;
    private readonly ComboBox sources = new() { DisplayMemberPath = nameof(CloudSource.Title) };
    private readonly ComboBox kinds = new() { ItemsSource = new[] { "chat", "work" }, SelectedIndex = 0 };
    private readonly TextBox url = new();
    private readonly TextBox title = new() { MaxLength = 200 };
    private readonly TextBox receipt = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox answers = new()
    {
        IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel controls = new();
    private readonly Button pullWork;
    private readonly ComboBox workChoices = new() { DisplayMemberPath = nameof(WorkSourceCandidate.Title) };
    private string? discoveryOperation;
    private long cursor;
    private bool busy;
    private bool refreshing;

    public CloudImportsWindow(RelayClient relay, CancellationToken lifetime)
    {
        this.relay = relay;
        this.lifetime = lifetime;
        Title = "取り込んだクラウド回答";
        Width = 600;
        Height = 630;
        MinWidth = 370;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(12) };
        var top = new ScrollViewer { MaxHeight = 370, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = controls };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(answers);
        Content = root;

        Add("登録済み会話", sources);
        Add("種別", kinds);
        Add("ChatGPTの会話URL（共有リンク不要）", url);
        Add("会話名（初回に保存。後で変更可）", title);
        var actions = new WrapPanel();
        actions.Children.Add(Button("登録・更新", async () => await SaveAsync()));
        actions.Children.Add(Button("再読込", async () => await ReloadAsync()));
        actions.Children.Add(Button("次の回答", async () => await RunAsync(() => ReadAsync(reset: false))));
        pullWork = Button("Workの最新回答を取得", async () => await PullWorkAsync());
        pullWork.IsEnabled = false;
        actions.Children.Add(pullWork);
        actions.Children.Add(Button("Work候補を探す", async () => await DiscoverWorkAsync()));
        controls.Children.Add(actions);
        Add("取得可能なWork候補", workChoices);
        controls.Children.Add(Button("選んだWorkを登録", async () => await RegisterWorkAsync()));
        Add("元の会話で明示転送するときに使う登録ID", receipt);
        controls.Children.Add(status);
        sources.SelectionChanged += async (_, _) =>
        {
            if (refreshing) return;
            ApplySelection();
            await RunAsync(() => ReadAsync(reset: true));
        };
        Loaded += async (_, _) => await ReloadAsync();
    }

    private void Add(string name, Control control)
    {
        controls.Children.Add(new TextBlock { Text = name, Margin = new Thickness(0, 4, 0, 0) });
        controls.Children.Add(control);
    }

    private static Button Button(string label, Func<Task> click)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 6, 6, 6), Padding = new Thickness(8, 3, 8, 3) };
        button.Click += async (_, _) => await click();
        return button;
    }

    private Task ReloadAsync() => RunAsync(async () =>
    {
        var selected = (sources.SelectedItem as CloudSource)?.Id;
        await LoadSourcesAsync(selected);
        await ReadAsync(reset: true);
    });

    private Task SaveAsync() => RunAsync(async () =>
    {
        var kind = (string?)kinds.SelectedItem ?? "chat";
        var saved = await relay.SaveCloudSourceAsync(new SaveCloudSourceRequest(kind, url.Text, title.Text), lifetime);
        await LoadSourcesAsync(saved.Id);
        await ReadAsync(reset: true);
    });

    private async Task LoadSourcesAsync(string? selected)
    {
        var list = await relay.GetCloudSourcesAsync(lifetime);
        refreshing = true;
        try
        {
            sources.ItemsSource = list;
            sources.SelectedItem = list.FirstOrDefault(source => source.Id == selected) ?? list.FirstOrDefault();
            ApplySelection();
        }
        finally { refreshing = false; }
    }

    private void ApplySelection()
    {
        pullWork.IsEnabled = sources.SelectedItem is CloudSource { Kind: "work" };
        if (sources.SelectedItem is not CloudSource source)
        {
            receipt.Clear();
            return;
        }
        kinds.SelectedItem = source.Kind;
        url.Text = source.Url;
        title.Text = source.Title;
        receipt.Text = source.Id;
    }

    private Task DiscoverWorkAsync() => RunAsync(async () =>
    {
        discoveryOperation = null;
        workChoices.ItemsSource = null;
        status.Text = "取得可能なWorkを問い合わせています。";
        var result = await WaitForWorkAsync(await relay.DiscoverWorkAsync(lifetime));
        if (!IsLoaded) return;
        discoveryOperation = result.State == "choices" ? result.OperationId : null;
        workChoices.ItemsSource = result.Choices;
        if (result.Choices is { Length: > 0 }) workChoices.SelectedIndex = 0;
        status.Text = result.Choices is { Length: > 0 } ? "登録するWorkを選んでください。" :
            result.State == "choices" ? "取得可能なWorkは見つかりませんでした。" : "Work候補の確認が時間内に完了しませんでした。";
    });

    private Task RegisterWorkAsync() => RunAsync(async () =>
    {
        if (discoveryOperation is not { } operation || workChoices.SelectedItem is not WorkSourceCandidate selected)
        {
            status.Text = "先に取得可能なWork候補を探してください。";
            return;
        }
        var source = await relay.RegisterDiscoveredWorkAsync(operation, selected.ConversationId, lifetime);
        await LoadSourcesAsync(source.Id);
        await ReadAsync(reset: true);
    });

    private async Task<WorkPullStatus> WaitForWorkAsync(WorkPullStatus state)
    {
        while (IsLoaded && state.State == "pending")
        {
            await Task.Delay(TimeSpan.FromSeconds(2), lifetime);
            if (!IsLoaded) break;
            state = await relay.ReadWorkRetrievalAsync(state.OperationId, lifetime);
        }
        return state;
    }

    private Task PullWorkAsync() => RunAsync(async () =>
    {
        if (sources.SelectedItem is not CloudSource { Kind: "work" } source) return;
        var state = await relay.StartWorkRetrievalAsync(source.Id, lifetime);
        status.Text = "Workの回答取得を依頼しました。";
        state = await WaitForWorkAsync(state);
        if (!IsLoaded) return;
        if (state.Answer is { } answer)
        {
            answers.Text = answer.Text;
            answers.ScrollToHome();
            status.Text = "Workの回答を取得しました。";
        }
        else status.Text = state.State switch
        {
            "unavailable" => "Work回答の原文を取得できませんでした。",
            "not_authorized" => "このWorkを読み取る権限がありません。",
            "not_found" => "Workまたは完了した回答が見つかりません。",
            _ => "取得が時間内に完了しませんでした。"
        };
    });

    private async Task ReadAsync(bool reset)
    {
        if (sources.SelectedItem is not CloudSource source)
        {
            answers.Clear();
            status.Text = "まず会話URLと会話名を登録してください。";
            return;
        }
        if (reset) answers.Clear();
        var page = await relay.ReadCloudAnswersAsync(source.Id, reset ? 0 : cursor, lifetime);
        cursor = page.NextCursor;
        // Keep one page at a time even with very long answers.
        if (page.Answers.Length > 0 || reset)
            answers.Text = string.Join(Environment.NewLine + Environment.NewLine, page.Answers.Select(answer =>
                $"{answer.ReceivedAt.ToLocalTime():yyyy/MM/dd HH:mm}{Environment.NewLine}{answer.Text}"));
        answers.ScrollToHome();
        status.Text = page.HasMore ? "続きがあります。「次の回答」で表示します。" :
            page.Answers.Length == 0 && reset ? "取り込んだ回答はまだありません。" : "ここまで表示しました。";
    }

    private async Task RunAsync(Func<Task> work)
    {
        if (busy || lifetime.IsCancellationRequested) return;
        busy = true;
        controls.IsEnabled = false;
        try { await work(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
        {
            status.Text = "読み書きできませんでした。" + error.Message;
        }
        finally
        {
            controls.IsEnabled = true;
            busy = false;
        }
    }
}
