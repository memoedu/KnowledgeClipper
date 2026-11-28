using System.IO;
using System.Net.Http;
using System.Reflection;
using KnowledgeClipper.Workflow;

internal static partial class Program
{
    private static T? NoCapturedContext<T>(string text, string granularity) => default;

    private static async Task DesktopSshAsync(Form owner, string sshFixture, bool expectMissingCredential)
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "isolated-test-fixture-not-a-real-credential");
        Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://127.0.0.1:1");
        using var importer = new QuickLanguageImportFixture();
        var settings = Settings() with
        {
            ApiBaseUrl = "https://api.openai.com/v1", Model = "gpt-6-luna", ImageModel = "gpt-6.2-sol",
            QuickLanguage = true, ContentTimeoutSeconds = 30, ContentMaxRetries = 1, Practice = PracticeChoice.Reading,
            OriginalExcerptHotkey = "", ReplayLastHotkey = "", ClipboardImageDotEnabled = false,
            RemNoteSdkEndpoint = importer.Url, RemNoteRootId = "fixture-root", ModeDestinations = new() { ["language-en"] = ExportDestination.RemNote }
        };
        WorkflowJson.Save(Path.Combine(dataRoot, "language-settings.json"), settings);
        var binding = Path.Combine(dataRoot, "desktop-content-ssh.json");
        File.Copy(sshFixture, binding);
        var configType = Type("AppConfig");
        var config = configType.GetMethod("Read")!.Invoke(null, [Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json")])!;
        configType.GetProperty("WorkDirectory")!.SetValue(config, dataRoot);
        var controllerType = Type("Desktop.Core.DesktopController");
        var controller = controllerType.GetConstructors(Flags).Single(c => c.GetParameters().Length == 5).Invoke(new object?[5]);
        using var main = (Form)New("BoundClipperForm", config, controller, false);
        Call(main, "EnableLanguageWorkflow");
        main.Show();
        await Until(() => (bool)main.GetType().GetField("languageWindowShown", Flags)!.GetValue(main)!,
            "the isolated desktop finished normal startup before the frozen selection is injected");
        var language = main.GetType().GetField("languageFlow", Flags)!.GetValue(main)!;
        using var contentHttp = new HttpClient(new ImageRouteProvider());
        var httpField = language.GetType().GetField("http", Flags)!;
        ((HttpClient)httpField.GetValue(language)!).Dispose();
        httpField.SetValue(language, contentHttp);

        var capture = language.GetType().GetField("capture", Flags)!.GetValue(language)!;
        capture.GetType().GetField("readForeground", Flags)!.SetValue(capture, (Func<long>)(() => owner.Handle.ToInt64()));
        var contextField = capture.GetType().GetField("readContext", Flags)!;
        contextField.SetValue(capture, typeof(Program).GetMethod(nameof(NoCapturedContext), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(contextField.FieldType.GenericTypeArguments.Last()).CreateDelegate(contextField.FieldType));
        var monitor = main.GetType().GetField("selectionMonitor", Flags)!.GetValue(main)!;
        monitor.GetType().GetField("running", Flags)!.SetValue(monitor, true);
        monitor.GetType().GetField("readForeground", Flags)!.SetValue(monitor, (Func<long>)(() => owner.Handle.ToInt64()));
        var state = monitor.GetType().GetField("state", Flags)!.GetValue(monitor)!;
        Call(state, "Start");
        var dot = (Form)main.GetType().GetField("selectionDot", Flags)!.GetValue(main)!;
        void ClickDot(string action, string text)
        {
            var window = owner.Handle.ToInt64();
            var pid = (uint)Environment.ProcessId;
            var point = new Point(100, 100);
            var snapshot = New("SelectionSnapshot", text, window, "Public SSH fixture", point.X, point.Y,
                DateTimeOffset.UtcNow, 0L, 0L, pid);
            var ticket = Call(state, "BeginRead", window, pid, point);
            var current = Call(state, "Publish", ticket, snapshot, window, pid, Environment.TickCount64);
            Call(dot, "ShowSelection", current);
            ((Button)dot.Controls.Find(dot.Name + "." + action, true).Single()).PerformClick();
        }

        ClickDot("read", "lead");
        await Until(() => Forms(language).Length == 1, "the actual floating Read button falls back to the original native review", 37000);
        var form = Forms(language).Single();
        var parent = (ClipDraft)Get(form, "Draft")!;
        var store = new DraftStore(dataRoot);
        var retry = (Button)form.Controls.Find("knowledge-clipper.language.retry-content", true).Single();
        var confirm = (Button)form.Controls.Find("knowledge-clipper.language.confirm", true).Single();
        ClipDraft? Child() => parent.ModeCards.Count == 1 ? LanguageModeCards.Children(parent, store).Single() : null;
        if (expectMissingCredential)
        {
            bool MissingCredential() => retry.Enabled && Child()?.ContentError?.Contains(
                "Mac 剪藏未配置 OpenAI 专用凭据。原草稿已保留，Mac 尚未提交本次内容请求。") == true;
            await Until(MissingCredential, "the real Mac worker reports its missing application credential in the original review", 37000);
            var unresolved = Child()!;
            var statePath = Path.Combine(dataRoot, "desktop-content-ssh", unresolved.Id);
            Check(unresolved.ImportState == ImportState.Unconfirmed && unresolved.Content == null &&
                Directory.GetFiles(statePath, "*.request.json").Length == 1 &&
                Directory.GetFiles(statePath, "*.response.json").Length == 0 && !confirm.Enabled && importer.ExportCount == 0,
                "no content receipt was consumed and the selected draft still cannot export");
            retry.PerformClick();
            await Until(MissingCredential, "retrying the same draft returns the same confirmed pre-submission failure", 37000);
            Check(Child()!.Id == unresolved.Id && parent.Original.SelectedText == "lead" &&
                Directory.GetFiles(statePath, "*.request.json").Length == 1 &&
                Directory.GetFiles(statePath, "*.response.json").Length == 0 && importer.ExportCount == 0,
                "retry preserves one original request identity, no response and no export");
            Save(form, "missing-credential-review.png");
            main.Close();
            return;
        }
        bool AwaitingReply() => retry.Enabled && Child()?.ContentError?.Contains("Mac 内容结果尚未确认") == true;
        await Until(AwaitingReply, "original review enabled retry after transport failure before the remote claim", 37000);
        var childId = Child()!.Id;
        var receipts = Path.Combine(dataRoot, "desktop-content-ssh", childId);
        Check(Directory.GetFiles(receipts, "*.request.json").Length == 1 && importer.ExportCount == 0 && !confirm.Enabled,
            "the selected material has one pending request and no export before review");
        File.Move(binding, binding + ".disabled");
        retry.PerformClick();
        await Until(AwaitingReply, "original review retains the same draft when reply is lost after the remote claim", 37000);
        Check(Child()!.Id == childId && Directory.GetFiles(receipts, "*.response.json").Length == 0 && importer.ExportCount == 0,
            "disabling the global switch still reuses the frozen route of the original child");
        retry.PerformClick();
        await Until(() => Child()?.Content != null && confirm.Enabled, "the original review recovers the Mac response", 37000);
        var child = Child()!;
        Check(child.Id == childId && parent.Input.Source == "selection-dot" && parent.Original.SelectedText == "lead" &&
            child.Settings.Model == "gpt-6-luna" && child.Content!.Fields!.SentenceTranslationZh == "我们每天练习 lead。" &&
            Directory.GetFiles(receipts, "*.response.json").Length == 1 && importer.ExportCount == 0,
            "one frozen provider receipt reaches the original selected-text draft and approval screen");
        Save(form, "original-review.png");
        confirm.PerformClick();
        await Until(() => Child()?.ImportState == ImportState.Verified, "the original confirmation verifies one Windows loopback export", 12000);
        Check(importer.ExportCount == 1 && Child()!.ConfirmedRemNote!.RootRemId == "fixture-root" &&
            Child()!.RemNoteResult!.RemIds.Length == 1, "original export retains the configured root and has exactly one verified receipt");

        foreach (var active in Forms(language)) active.Close();
        ClickDot("ask", "为什么矩形面积等于长乘宽？");
        await Until(() => ChatForms(language).Length == 1, "the actual floating Ask button opens the original chat window");
        var ask = ChatForms(language).Single();
        var question = (ClipDraft)Get(ask, "Draft")!;
        var sendPending = (bool)ask.GetType().GetField("initialSendPending", Flags)!.GetValue(ask)!;
        ask.Close();
        Check(question.Settings.ApiBaseUrl == "https://api.openai.com/v1" && question.Chat is
            { Binding.Provider: "api", Model: "gpt-6.2-sol", TypeId: "fast-answer" } && sendPending &&
            !Directory.Exists(Path.Combine(dataRoot, "desktop-content-ssh", question.Id)),
            "quick Ask still selects its original OpenAI model and single automatic-send path, outside the SSH transport");
        main.Close();
    }
}
