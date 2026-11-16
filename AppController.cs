using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace SimpleVPN
{
    public sealed class AppController
    {
        private readonly LocalStore store;
        private readonly ProxyManager proxy;
        private readonly Func<string, int, OperationCancel, string> download;
        private readonly Func<int, OperationCancel, bool> check;
        private readonly bool monitorEnabled;
        private readonly bool guardianEnabled;
        private readonly bool preparationEnabled;
        private readonly OperationCancel cancel = new OperationCancel();
        // Only short process/proxy transitions hold this lock; HTTPS never does.
        private readonly object transition = new object();
        private CoreProcess core;
        private CorePreparation prepared;
        private int preparationGeneration;
        private Process guardian;
        private System.Windows.Forms.Timer monitor;
        private volatile bool closing;
        private OperationCancel connectionCancel;
        private readonly object latencyGate = new object();
        private readonly Dictionary<string,int> latencies = new Dictionary<string,int>();
        public Dictionary<string,int> Latencies { get { lock(latencyGate) return new Dictionary<string,int>(latencies); } }
        public bool IsPinging { get; private set; }
        public bool IsConnectionPrepared { get { lock(transition) return prepared != null && prepared.Ready && prepared.IsAlive; } }
        public bool CanCancelConnection { get { return IsBusy && connectionCancel != null && !connectionCancel.IsCancellationRequested; } }
        public SubscriptionMetadata Metadata { get { return Settings.Metadata ?? new SubscriptionMetadata(); } }
        public AppSettings CopySettings() { return LocalStore.Json().Deserialize<AppSettings>(LocalStore.Json().Serialize(Settings)); }
        private ConfigLibrary library = new ConfigLibrary();
        // UI summaries intentionally omit Body: repainting a server list must
        // not reserialize every stored subscription or duplicate credentials.
        public List<ConfigSource> Sources { get {
            lock(transition) {
                var result = new List<ConfigSource>();
                foreach(var source in library.Sources) result.Add(new ConfigSource { Id=source.Id, Name=source.Name, Url=source.Url,
                    SelectedServerId=source.SelectedServerId, LastUpdatedUtc=source.LastUpdatedUtc,
                    Metadata=LocalStore.Json().Deserialize<SubscriptionMetadata>(LocalStore.Json().Serialize(source.Metadata ?? new SubscriptionMetadata())),
                    HiddenServerIds=source.HiddenServerIds == null ? new List<string>() : new List<string>(source.HiddenServerIds) });
                return result;
            }
        } }
        private ConfigSource CurrentSource { get { return library.Sources.Find(s => s.Id == Settings.SelectedSourceId); } }
        private List<ServerProfile> pendingServers;
        private DateTime lastRefreshAttempt = DateTime.UtcNow;
        public AppSettings Settings { get; private set; }
        public List<ServerProfile> Servers { get; private set; }
        public event EventHandler Changed;
        public string StatusText { get; private set; }
        public string DetailText { get; private set; }
        public string Notice { get; private set; }
        public bool IsConnected { get; private set; }
        public bool IsBusy { get; private set; }

        public AppController(LocalStore local) : this(local, null, Network.Download, Network.Check, true) { }
        // The fake-platform overload keeps lifecycle regression tests away from Windows proxy settings.
        public AppController(LocalStore local, IProxyPlatform platform, Func<string, int, OperationCancel, string> downloader, Func<int, OperationCancel, bool> checker, bool enableMonitor)
            : this(local, platform, downloader, checker, enableMonitor, platform == null) { }
        public AppController(LocalStore local, IProxyPlatform platform, Func<string, int, OperationCancel, string> downloader, Func<int, OperationCancel, bool> checker, bool enableMonitor, bool enablePreparation)
        {
            store = local; proxy = platform == null ? new ProxyManager(store) : new ProxyManager(store, platform);
            download = downloader; check = checker; monitorEnabled = enableMonitor; guardianEnabled = platform == null;
            preparationEnabled = enablePreparation;
            Settings = new AppSettings();
            Servers = new List<ServerProfile>(); StatusText = "Отключено";
            DetailText = "Выберите сервер и нажмите кнопку"; Notice = "";
        }
        private void Update() { EventHandler handler = Changed; if (!closing && handler != null) handler(this, EventArgs.Empty); }

        private WorkTicket BeginWork(Action operation, Action after)
        {
            var ticket = new WorkTicket();
            if (IsBusy || closing) { ticket.Complete(null); return ticket; }
            IsBusy = true; Update();
            var worker = new BackgroundWorker();
            worker.DoWork += delegate { operation(); };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs args)
            {
                IsBusy = false;
                Exception error = args.Error;
                if (error is OperationCancelledException) error = null;
                if (error != null && !closing)
                {
                    store.Log("OPERATION_FAILED");
                    Notice = "Не удалось завершить действие. Попробуйте ещё раз.";
                }
                Update();
                if (!closing && after != null && error == null) after();
                PrepareSelected();
                ticket.Complete(error);
                worker.Dispose();
            };
            worker.RunWorkerAsync();
            return ticket;
        }

        // Prepare one loopback-only core while idle. This never applies the
        // Windows proxy or performs a remote request. A real HTTPS check still
        // runs on every press of Connect, including when this core is ready.
        private void PrepareSelected()
        {
            if (!preparationEnabled || closing || IsBusy || IsConnected || IsPinging) return;
            CorePreparation previous = null;
            ServerProfile server;
            AppSettings preferences;
            string key;
            int generation;
            try
            {
                lock(transition)
                {
                    if(closing || IsBusy || IsConnected || IsPinging || core != null || File.Exists(proxy.Journal)) return;
                    server = Servers.Find(s => s.Id == Settings.SelectedServerId);
                    if(server == null) return;
                    preferences = CopySettings();
                    key = ConnectionOptions.BuildConfig(server, 49151, preferences);
                    if(prepared != null && prepared.Key == key) return;
                    previous = prepared; prepared = null;
                    generation = ++preparationGeneration;
                    if(previous == null)
                    {
                        prepared = new CorePreparation(CorePath, server, preferences, key);
                        return;
                    }
                }
                // Process.Start can be slow on an old HDD. Stop a superseded
                // preparation off the UI thread before starting its replacement.
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        previous.Dispose();
                        lock(transition)
                        {
                            if(generation != preparationGeneration || closing || IsBusy || IsConnected || IsPinging || core != null || prepared != null || File.Exists(proxy.Journal)) return;
                            prepared = new CorePreparation(CorePath, server, preferences, key);
                        }
                    }
                    catch { store.Log("PREPARATION_FAILED"); }
                });
            }
            catch { store.Log("PREPARATION_FAILED"); }
        }
        private string CorePath { get { return Path.Combine(Path.Combine(store.Root, "core"), "xray.exe"); } }
        private void StopPreparation()
        {
            CorePreparation previous;
            lock(transition) { ++preparationGeneration; previous = prepared; prepared = null; }
            if(previous != null) previous.Dispose();
        }

        public WorkTicket InitializeAsync()
        {
            return BeginWork(delegate
            {
                try
                {
                    lock (transition)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (proxy.Restore(false)) Notice = "Предыдущее подключение завершено. Интернет восстановлен.";
                    }
                    try { Settings = store.LoadSettings(); }
                    catch { store.Log("SETTINGS_UNREADABLE"); Notice = "Введите подписку заново: сохранённые настройки принадлежат другому пользователю Windows или повреждены."; }
                    try
                    {
                        string cached = store.Load<string>("servers.dpapi");
                        if (!String.IsNullOrEmpty(cached)) Servers = SubscriptionParser.Parse(cached);
                    }
                    catch { store.Log("CACHE_UNREADABLE"); }
                    LoadSources();
                    SelectAvailable();
                    if (!String.IsNullOrEmpty(Settings.SubscriptionUrl) && (Servers.Count == 0 || (guardianEnabled && (Metadata.RetrievedUtc == DateTime.MinValue || DateTime.UtcNow - Metadata.RetrievedUtc > TimeSpan.FromMinutes(5)))))
                        RefreshInternal(Settings.SubscriptionUrl);
                }
                catch (OperationCancelledException) { }
                catch { store.Log("INITIALIZE_FAILED"); Notice = "Не удалось подготовить подключение. Проверьте, что папка приложения доступна для записи."; }
            }, delegate
            {
                if (monitorEnabled) { monitor = new System.Windows.Forms.Timer { Interval = 2000 }; monitor.Tick += MonitorTick; monitor.Start(); }
                if (Settings.AutoConnect && Settings.AutoSelectFastest && Servers.Count > 0) PingAllAsync().OnCompleted(delegate(Exception error) { if(!closing && error==null)ToggleAsync(); });
                else {
                    // Give the foreground connection first use of a slow CPU/HDD.
                    // An optional full subscription scan can start afterwards.
                    if (Settings.AutoConnect && Servers.Count > 0)
                        ToggleAsync().OnCompleted(delegate { if(!closing && Settings.PingOnOpen) PingAllAsync(); });
                    else if(Settings.PingOnOpen && Servers.Count > 0) PingAllAsync();
                }
            });
        }

        private void SelectAvailable()
        {
            if (Servers.Count == 0) { Settings.SelectedServerId = ""; return; }
            if (!Servers.Exists(s => s.Id == Settings.SelectedServerId))
            {
                if (!String.IsNullOrEmpty(Settings.SelectedServerId)) Notice = "Прежний сервер исчез из подписки. Выбран первый доступный.";
                Settings.SelectedServerId = Servers[0].Id;
            }
        }
        private void LoadSources()
        {
            bool saved = File.Exists(Path.Combine(store.Data, "sources.dpapi"));
            if(saved)
            {
                library = store.Load<ConfigLibrary>("sources.dpapi") ?? new ConfigLibrary();
                if(library.Sources == null) library.Sources = new List<ConfigSource>();
            }
            else if(!String.IsNullOrEmpty(Settings.SubscriptionUrl) || Servers.Count > 0)
            {
                var source = new ConfigSource { Id = Guid.NewGuid().ToString("N"), Name = String.IsNullOrEmpty(Metadata.Title) ? "Моя подписка" : Metadata.Title,
                    Url = Settings.SubscriptionUrl, Body = store.Load<string>("servers.dpapi") ?? "", Metadata = Metadata,
                    LastUpdatedUtc = Settings.LastUpdatedUtc, SelectedServerId = Settings.SelectedServerId };
                library.Sources.Add(source); Settings.SelectedSourceId = source.Id;
                SaveSources();
            }
            ConfigSource selected = CurrentSource;
            if(selected == null && library.Sources.Count > 0) selected = library.Sources[0];
            ActivateSource(selected);
        }
        private List<ServerProfile> VisibleProfiles(ConfigSource source)
        {
            if(source.HiddenServerIds == null) source.HiddenServerIds = new List<string>();
            if(String.IsNullOrEmpty(source.Body)) return new List<ServerProfile>();
            return SubscriptionParser.Parse(source.Body).FindAll(s => !source.HiddenServerIds.Contains(s.Id));
        }
        private void ActivateSource(ConfigSource source)
        {
            Settings.SelectedSourceId = source == null ? "" : source.Id;
            Settings.SubscriptionUrl = source == null ? "" : source.Url;
            Settings.Metadata = source == null ? new SubscriptionMetadata() : source.Metadata ?? new SubscriptionMetadata();
            Settings.ApplyProviderUpdateInterval();
            Settings.LastUpdatedUtc = source == null ? DateTime.MinValue : source.LastUpdatedUtc;
            Settings.SelectedServerId = source == null ? "" : source.SelectedServerId;
            Servers = source == null ? new List<ServerProfile>() : VisibleProfiles(source);
            pendingServers = null;
            SelectAvailable();
            lock(latencyGate) latencies.Clear();
        }
        private void SaveSources()
        {
            ConfigSource selected = CurrentSource;
            if(selected != null) selected.SelectedServerId = Settings.SelectedServerId;
            store.Save("sources.dpapi", library);
            store.Save("settings.dpapi", Settings);
        }
        public WorkTicket SelectSourceAsync(string id)
        {
            return BeginWork(delegate {
                if(IsPinging) { Notice = "Дождитесь завершения проверки серверов."; return; }
                lock(transition)
                {
                    ConfigSource next = library.Sources.Find(s => s.Id == id);
                    if(next == null || next == CurrentSource) return;
                    if(IsConnected && !Disconnect()) return;
                    SaveSources(); ActivateSource(next); SaveSources();
                    Notice = "Выбрана подписка. Нажмите кнопку подключения.";
                }
            }, null);
        }
        public WorkTicket AddSourceAsync(string name, string input)
        {
            return BeginWork(delegate {
                if(IsPinging) { Notice = "Дождитесь завершения проверки серверов."; return; }
                if(Compat.IsBlank(input)) throw new ArgumentException("Введите ссылку подписки или конфигурацию.");
                input = input.Trim();
                var source = new ConfigSource { Id = Guid.NewGuid().ToString("N"), Name = Compat.IsBlank(name) ? "Мои конфиги" : name.Trim() };
                if(source.Name.Length > 120) source.Name = source.Name.Substring(0,120);
                if(input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    source.Url = Network.SubscriptionUri(input).AbsoluteUri;
                    if(library.Sources.Exists(s => s.Url == source.Url)) { Notice = "Эта подписка уже добавлена."; return; }
                    int port; lock(transition) port = IsConnected && core != null ? core.Port : 0;
                    SubscriptionDownload response = guardianEnabled ? Network.DownloadSubscription(source.Url,port,cancel) : null;
                    source.Body = response == null ? download(source.Url,port,cancel) : response.Body;
                    if(response != null) source.Metadata = response.Metadata;
                    if(Compat.IsBlank(name) && !String.IsNullOrEmpty(source.Metadata.Title)) source.Name = source.Metadata.Title;
                }
                else source.Body = input;
                int skipped; SubscriptionParser.Parse(source.Body, out skipped); cancel.ThrowIfCancellationRequested();
                source.LastUpdatedUtc = DateTime.UtcNow;
                lock(transition)
                {
                    if(IsConnected && !Disconnect()) return;
                    SaveSources(); library.Sources.Add(source); ActivateSource(source); SaveSources();
                    Notice = skipped > 0 ? "Добавлены поддерживаемые серверы. Не удалось прочитать конфигураций: " + skipped + "." : "Конфигурации добавлены. Выберите сервер и подключитесь.";
                }
            }, null);
        }
        public WorkTicket DeleteSourceAsync(string id)
        {
            return BeginWork(delegate {
                if(IsPinging) { Notice = "Дождитесь завершения проверки серверов."; return; }
                lock(transition)
                {
                    ConfigSource source = library.Sources.Find(s => s.Id == id);
                    if(source == null) return;
                    bool selected = source == CurrentSource;
                    if(selected && IsConnected && !Disconnect()) return;
                    SaveSources(); library.Sources.Remove(source);
                    if(selected) ActivateSource(library.Sources.Count > 0 ? library.Sources[0] : null);
                    SaveSources(); Notice = "Подписка удалена.";
                }
            }, null);
        }
        public WorkTicket RemoveServerAsync(string id)
        {
            return BeginWork(delegate {
                if(IsPinging) { Notice = "Дождитесь завершения проверки серверов."; return; }
                lock(transition)
                {
                    ConfigSource source = CurrentSource;
                    if(source == null || !Servers.Exists(s => s.Id == id)) return;
                    if(Settings.SelectedServerId == id && IsConnected && !Disconnect()) return;
                    if(source.HiddenServerIds == null) source.HiddenServerIds = new List<string>();
                    if(!source.HiddenServerIds.Contains(id)) source.HiddenServerIds.Add(id);
                    Servers = VisibleProfiles(source); pendingServers = null; SelectAvailable(); SaveSources();
                    Notice = "Сервер удалён из списка.";
                }
            }, null);
        }
        public WorkTicket RestoreServersAsync()
        {
            return BeginWork(delegate {
                if(IsPinging) { Notice = "Дождитесь завершения проверки серверов."; return; }
                lock(transition) {
                    ConfigSource source = CurrentSource; if(source == null) return;
                    source.HiddenServerIds = new List<string>(); Servers = VisibleProfiles(source); SelectAvailable(); SaveSources();
                    Notice = "Удалённые серверы восстановлены.";
                }
            }, null);
        }
        public void SelectServer(ServerProfile server)
        {
            if (IsBusy || closing || server == null || server.Id == Settings.SelectedServerId) return;
            bool reconnect = IsConnected;
            Settings.SelectedServerId = server.Id;
            try { SaveSources(); }
            catch { store.Log("SAVE_SELECTION_FAILED"); Notice = "Не удалось запомнить выбранный сервер."; }
            Update();
            if(reconnect) ToggleAsync().OnCompleted(delegate(Exception error) { if(error==null && !closing && !IsConnected)ToggleAsync(); });
            else PrepareSelected();
        }
        private void RefreshInternal(string url)
        {
            try
            {
                int port;
                lock (transition) { cancel.ThrowIfCancellationRequested(); port = IsConnected && core != null ? core.Port : 0; }
                SubscriptionDownload response = guardianEnabled ? Network.DownloadSubscription(url, port, cancel) : null;
                string text = response == null ? download(url, port, cancel) : response.Body;
                int skipped;
                List<ServerProfile> parsed = SubscriptionParser.Parse(text, out skipped);
                cancel.ThrowIfCancellationRequested();
                lock (transition)
                {
                    cancel.ThrowIfCancellationRequested();
                    ConfigSource source = CurrentSource;
                    if(source == null)
                    {
                        source = new ConfigSource { Id = Guid.NewGuid().ToString("N"), Name = "Моя подписка", Url = url };
                        library.Sources.Add(source); Settings.SelectedSourceId = source.Id;
                    }
                    source.Url = url; source.Body = text;
                    parsed = VisibleProfiles(source);
                    if (IsConnected) pendingServers = parsed; else Servers = parsed;
                    Settings.SubscriptionUrl = url; Settings.LastUpdatedUtc = DateTime.UtcNow;
                    if(response != null) {
                        Settings.Metadata = response.Metadata;
                        Settings.ApplyProviderUpdateInterval();
                    }
                    source.LastUpdatedUtc = Settings.LastUpdatedUtc; source.Metadata = Settings.Metadata;
                    if(source.Name == "Моя подписка" && !String.IsNullOrEmpty(Settings.Metadata.Title)) source.Name = Settings.Metadata.Title;
                    lock(latencyGate) latencies.Clear();
                    if (!IsConnected) SelectAvailable();
                    SaveSources();
                    Notice = IsConnected ? "Серверы обновлены. Новый список появится после отключения." : "Список серверов обновлён.";
                    if(skipped > 0) Notice += " Не удалось прочитать конфигураций: " + skipped + ".";
                }
                store.Log("SUBSCRIPTION_UPDATED");
            }
            catch (OperationCancelledException) { throw; }
            catch
            {
                cancel.ThrowIfCancellationRequested();
                store.Log("SUBSCRIPTION_UPDATE_FAILED");
                Notice = Servers.Count > 0 ? "Не удалось обновить серверы. Сохранённый список доступен." : "Не удалось загрузить серверы. Проверьте интернет и ссылку подписки.";
            }
        }
        public WorkTicket RefreshAsync()
        {
            return BeginWork(delegate
            {
                if (String.IsNullOrEmpty(Settings.SubscriptionUrl)) { Notice = CurrentSource == null ? "Сначала добавьте подписку или конфигурации." : "Это локальные конфигурации. Их можно удалить или добавить заново."; return; }
                RefreshInternal(Settings.SubscriptionUrl);
            }, null);
        }
        public WorkTicket SaveSettingsAsync(string url, bool startup, bool auto)
        {
            return BeginWork(delegate
            {
                Network.SubscriptionUri(url); cancel.ThrowIfCancellationRequested();
                store.SetStartup(startup); Settings.StartWithWindows = startup; Settings.AutoConnect = auto;
                Settings.SubscriptionUrl = url.Trim(); store.Save("settings.dpapi", Settings);
                RefreshInternal(Settings.SubscriptionUrl);
            }, null);
        }
        public WorkTicket ToggleAsync()
        {
            return BeginWork(delegate
            {
                Notice = "";
                OperationCancel operation = null;
                IDisposable linked = null;
                CorePreparation standby = null;
                var elapsed = Stopwatch.StartNew();
                try
                {
                    CoreProcess attempt;
                    ServerProfile server;
                    AppSettings preferences;
                    lock (transition)
                    {
                        cancel.ThrowIfCancellationRequested();
                        if (IsConnected) { Disconnect(); return; }
                        server = Servers.Find(s => s.Id == Settings.SelectedServerId);
                        if (server == null) { Notice = "Добавьте подписку в настройках и выберите сервер."; return; }
                        StatusText = "Подключение..."; DetailText = server.Name;
                        operation = new OperationCancel(); connectionCancel = operation;
                        linked = cancel.Register(operation.Cancel);
                        preferences = CopySettings();
                        ++preparationGeneration; standby = prepared; prepared = null;
                    }
                    Update();
                    string key = ConnectionOptions.BuildConfig(server, 49151, preferences);
                    if(standby != null && (standby.Key != key || standby.Failed || (standby.Ready && !standby.IsAlive)))
                    { standby.Dispose(); standby = null; }
                    if(standby != null)
                    {
                        bool ready = standby.Ready;
                        attempt = standby.Take(operation);
                        lock(transition) { core = attempt; operation.ThrowIfCancellationRequested(); }
                        store.Log(ready ? "CORE_PREPARED_REUSED" : "CORE_PREPARATION_ADOPTED");
                    }
                    else
                    {
                        lock(transition) { operation.ThrowIfCancellationRequested(); attempt = new CoreProcess(CorePath); core = attempt; }
                        attempt.Start(server, operation, preferences);
                    }
                    store.Log("CORE_READY_MS_"+elapsed.ElapsedMilliseconds);
                    lock (transition)
                    {
                        operation.ThrowIfCancellationRequested();
                        if (guardianEnabled)
                        {
                            Process owner = Process.GetCurrentProcess();
                            guardian = Process.Start(new ProcessStartInfo(Path.Combine(store.Root, "SimpleVPN.exe"), "--watchdog " + owner.Id + " " + owner.StartTime.ToUniversalTime().Ticks)
                            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = store.Root });
                            if (guardian == null || guardian.HasExited) throw new IOException("WATCHDOG_START_FAILED");
                        }
                        proxy.Apply(attempt.Port);
                    }
                    store.Log("PROXY_READY_MS_"+elapsed.ElapsedMilliseconds);
                    bool verified = guardianEnabled ? Network.Check(attempt.Port, Settings.ConnectionTestUrl, Settings.ConnectionTimeoutSeconds, operation) : check(attempt.Port, operation);
                    operation.ThrowIfCancellationRequested();
                    if (!verified) throw new IOException("CONNECTIVITY_CHECK_FAILED");
                    lock (transition)
                    {
                        operation.ThrowIfCancellationRequested();
                        if (!attempt.IsAlive) throw new IOException("CORE_EXITED");
                        IsConnected = true; StatusText = "Подключено"; DetailText = server.Name; store.Log("CONNECTED");
                        store.Log("CONNECTED_MS_"+elapsed.ElapsedMilliseconds);
                    }
                }
                catch (OperationCancelledException) { lock (transition) Disconnect(); }
                catch (Exception error)
                {
                    bool cancelled = closing || (operation != null && operation.IsCancellationRequested);
                    ConnectionFailure failure = ConnectionFailure.FromException(error);
                    if(!cancelled) { store.Log("CONNECT_FAILED"); failure.WriteTo(store); }
                    lock (transition)
                    {
                        if (Disconnect() && !cancelled)
                        {
                            StatusText = "Ошибка подключения"; DetailText = failure.DetailText;
                            Notice = failure.Notice;
                        }
                    }
                }
                finally {
                    if(standby != null) standby.Dispose();
                    connectionCancel = null;
                    if(linked != null) linked.Dispose();
                    if(operation != null) operation.Dispose();
                }
            }, null);
        }

        public void CancelConnection() {
            OperationCancel current = connectionCancel;
            if(current == null) return;
            StatusText = "Отмена..."; Update();
            ThreadPool.QueueUserWorkItem(delegate {
                lock(transition) {
                    if(!Object.ReferenceEquals(connectionCancel,current))return;
                    current.Cancel(); Disconnect();
                }
                Update();
            });
        }

        public WorkTicket SavePreferencesAsync(AppSettings preferences) {
            return BeginWork(delegate {
                AppSettings validated = ConnectionOptions.Validate(preferences);
                cancel.ThrowIfCancellationRequested();
                bool changeUrl = validated.SubscriptionUrl != Settings.SubscriptionUrl;
                if(!String.IsNullOrEmpty(validated.SubscriptionUrl)) Network.SubscriptionUri(validated.SubscriptionUrl);
                store.SetStartup(validated.StartWithWindows);
                validated.SelectedServerId = Settings.SelectedServerId;
                validated.SelectedSourceId = Settings.SelectedSourceId;
                validated.FollowProviderUpdateInterval = Settings.FollowProviderUpdateInterval && validated.UpdateIntervalHours == Settings.UpdateIntervalHours;
                validated.Metadata = Settings.Metadata;
                validated.LastUpdatedUtc = Settings.LastUpdatedUtc;
                Settings = validated;
                SaveSources();
                Notice = IsConnected ? "Сохранено. Настройки подключения применятся при следующем включении." : "Настройки сохранены.";
                if(changeUrl && !String.IsNullOrEmpty(Settings.SubscriptionUrl)) RefreshInternal(Settings.SubscriptionUrl);
            },null);
        }

        public WorkTicket PingAllAsync() {
            var ticket = new WorkTicket();
            if(IsPinging || IsBusy || closing || Servers.Count == 0) { ticket.Complete(null); return ticket; }
            IsPinging = true;
            var list = new List<ServerProfile>(Servers);
            var preferences = CopySettings();
            Update();
            var worker = new BackgroundWorker();
            worker.DoWork += delegate {
                StopPreparation();
                int next = -1;
                ThreadStart scan = delegate {
                    for(;;) {
                        int index = Interlocked.Increment(ref next);
                        if(index >= list.Count || closing || cancel.IsCancellationRequested) return;
                        int result = -1;
                        try {
                            using(var candidate = new CoreProcess(Path.Combine(Path.Combine(store.Root,"core"),"xray.exe"))) {
                                candidate.Start(list[index],cancel,preferences);
                                var elapsed = Stopwatch.StartNew();
                                result = Network.Check(candidate.Port,preferences.ConnectionTestUrl,Math.Max(6,preferences.ConnectionTimeoutSeconds),cancel) ? (int)elapsed.ElapsedMilliseconds : -1;
                            }
                        } catch { if(cancel.IsCancellationRequested) return; }
                        lock(latencyGate) latencies[list[index].Id] = result;
                        Update();
                    }
                };
                var second = new Thread(scan) { IsBackground = true };
                second.Start(); scan(); second.Join();
            };
            worker.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs args) {
                IsPinging = false;
                if(!closing && Settings.AutoSelectFastest && !IsConnected && !IsBusy) {
                    ServerProfile best = null; int bestTime = Int32.MaxValue;
                    var results = Latencies;
                    foreach(var item in Servers) { int delay; if(results.TryGetValue(item.Id,out delay) && delay >= 0 && delay < bestTime) { best = item; bestTime = delay; } }
                    if(best != null) SelectServer(best);
                }
                Update(); PrepareSelected(); ticket.Complete(args.Error); worker.Dispose();
            };
            worker.RunWorkerAsync(); return ticket;
        }

        // Caller holds transition. Network operations do not prevent this recovery.
        private bool Disconnect()
        {
            bool restored = true;
            try { proxy.Restore(false); } catch { restored = false; store.Log("PROXY_RESTORE_FAILED"); }
            if (core != null) { core.Dispose(); core = null; }
            if (guardian != null)
            {
                if (restored) { try { if (!guardian.HasExited) guardian.Kill(); } catch { } }
                guardian.Dispose(); guardian = null;
            }
            IsConnected = false; StatusText = restored ? "Отключено" : "Нужно восстановить интернет";
            if (pendingServers != null)
            {
                Servers = pendingServers; pendingServers = null; SelectAvailable();
                try { SaveSources(); } catch { store.Log("SAVE_SELECTION_FAILED"); }
            }
            DetailText = restored ? "Выберите сервер и нажмите кнопку" : "Закройте приложение и откройте его снова";
            if (!restored) Notice = "Не удалось восстановить прежние настройки сети. Перезапустите приложение.";
            return restored;
        }
        private void MonitorTick(object sender, EventArgs args)
        {
            if (IsBusy || closing) return;
            if (IsConnected && (core == null || !core.IsAlive))
            {
                BeginWork(delegate
                {
                    lock (transition)
                    {
                        if (Disconnect()) { StatusText = "Ошибка подключения"; DetailText = "Попробуйте выбрать другой сервер"; Notice = "Подключение прервалось. Прежние настройки сети восстановлены."; }
                        store.Log("CORE_LOST");
                    }
                }, null);
                return;
            }
            // Usage/expiry headers should follow the provider much sooner than
            // its full-profile refresh interval. Fetching also refreshes profiles.
            int interval = Math.Min(15, AppSettings.ClampUpdateIntervalHours(Settings.UpdateIntervalHours)*60);
            if (!String.IsNullOrEmpty(Settings.SubscriptionUrl) && DateTime.UtcNow - Settings.LastUpdatedUtc > TimeSpan.FromMinutes(interval) && DateTime.UtcNow - lastRefreshAttempt > TimeSpan.FromMinutes(5))
            { lastRefreshAttempt = DateTime.UtcNow; RefreshAsync(); }
        }
        public void Shutdown()
        {
            if (closing) return;
            closing = true;
            cancel.Cancel();
            if (monitor != null) { monitor.Stop(); monitor.Dispose(); }
            StopPreparation();
            lock (transition) Disconnect();
        }
    }
}


