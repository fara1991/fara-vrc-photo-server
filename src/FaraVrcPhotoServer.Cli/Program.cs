using FaraVrcPhotoServer;

// 手元の確認用 CLI（常駐ソフト本体は E3 で作る）。
//   vrcserver scan --name <自分の VRChat の表示名> [--logs <ログのフォルダ>] [--history <history.jsonl>]   ログを読んで履歴に追記する
//   vrcserver show [--history <history.jsonl>] [--at "yyyy-MM-dd HH:mm:ss"]                               履歴を表示する（--at で、その時刻のアバター）
//   vrcserver tag <写真.png ...> [--history <history.jsonl>] [--backup <フォルダ>] [--dry-run]              写真に、撮影日時のアバターを追記する（--dry-run は書かない）
//   vrcserver watch --name <表示名> [--photos <フォルダ>] [--logs <フォルダ>] [--history <ファイル>] [--backup <フォルダ>] [--no-xmp]
//                                                                                                       写真フォルダを見張り、新しい写真に追記する（Ctrl+C で終了）
// 表示名は、履歴・写真・出力のどれにも出さない（ログから自分の行を選ぶためにだけ使う）。

var values = new Dictionary<string, string>(StringComparer.Ordinal);
var flags = new HashSet<string>(StringComparer.Ordinal) { };
var positional = new List<string>();
var booleanFlags = new[] { "dry-run", "no-xmp" };
for (var i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("--", StringComparison.Ordinal))
    {
        var key = args[i][2..];
        if (booleanFlags.Contains(key)) flags.Add(key);
        else if (i + 1 < args.Length) values[key] = args[++i];
    }
    else
    {
        positional.Add(args[i]);
    }
}

var history = new HistoryStore(values.GetValueOrDefault("history") ?? HistoryStore.DefaultPath);

static string Describe(PhotoResult r) => r.Info is null ? r.Message : $"{r.Message}（{r.Info.AvatarName}）";

switch (positional.FirstOrDefault())
{
    case "scan":
    {
        if (!values.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("--name で、自分の VRChat の表示名を指定してください（空のあいだは何も記録しません）");
            return 2;
        }
        var folder = values.GetValueOrDefault("logs") ?? LogScanner.DefaultLogFolder;
        var result = LogScanner.Scan(folder, name, history);
        Console.WriteLine($"ログ {result.Files} ファイルから {result.Events} 件のイベントを読み、{result.Added} 件を履歴（{history.Path}）に追記しました。");
        return 0;
    }
    case "show":
    {
        var events = history.Load();
        if (values.TryGetValue("at", out var at))
        {
            if (!DateTime.TryParse(at, out var local))
            {
                Console.Error.WriteLine("--at は yyyy-MM-dd HH:mm:ss の形で指定してください");
                return 2;
            }
            var taken = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
            var avatar = HistoryStore.AvatarAt(events, taken);
            Console.WriteLine(avatar is null ? "その時刻のアバターは、履歴にありません。" : $"{avatar.AvatarName}（{avatar.AvatarId ?? "ID なし"}・{avatar.Time:yyyy-MM-dd HH:mm:ss} から）");
            return 0;
        }
        foreach (var e in events) Console.WriteLine(e.ToJsonLine());
        return 0;
    }
    case "tag":
    {
        var files = positional.Skip(1).ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine("写真のファイルを指定してください");
            return 2;
        }
        var dry = flags.Contains("dry-run");
        var events = history.Load();
        var processor = new PhotoProcessor(() => events, new PhotoOptions(WriteXmp: !dry, BackupFolder: values.GetValueOrDefault("backup")));
        var failed = 0;
        foreach (var file in files)
        {
            var result = processor.Process(file);
            Console.WriteLine($"{Path.GetFileName(file)}: {result.Outcome} - {Describe(result)}{(dry && result.Info is not null ? "（--dry-run のため書いていません）" : "")}");
            if (result.Outcome == PhotoOutcome.Failed) failed++;
        }
        return failed == 0 ? 0 : 1;
    }
    case "watch":
    {
        if (!values.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine("--name で、自分の VRChat の表示名を指定してください");
            return 2;
        }
        var logs = values.GetValueOrDefault("logs") ?? LogScanner.DefaultLogFolder;
        var photos = values.GetValueOrDefault("photos") ?? PhotoNaming.DefaultPhotoFolder;
        if (!Directory.Exists(photos))
        {
            Console.Error.WriteLine($"写真のフォルダがありません: {photos}");
            return 1;
        }
        var first = LogScanner.Scan(logs, name, history);
        Console.WriteLine($"ログ {first.Files} ファイルを読み、履歴に {first.Added} 件を追記しました。");

        var options = new PhotoOptions(WriteXmp: !flags.Contains("no-xmp"), BackupFolder: values.GetValueOrDefault("backup"));
        PhotoWatcher? watcher = null;
        var processor = new PhotoProcessor(() =>
        {
            LogScanner.Scan(logs, name, history);                                                          // 写真の処理の前に、ログを読み直す（直前の切り替えを取りこぼさない）
            return history.Load();
        }, options);
        watcher = new PhotoWatcher(photos, path =>
        {
            var result = processor.Process(path);
            watcher?.IgnoreForAWhile(path);
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {Path.GetFileName(path)}: {result.Outcome} - {Describe(result)}");
        });
        Console.WriteLine($"写真のフォルダを見張っています: {photos}（Ctrl+C で終了）");
        using var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };
        stop.Wait();
        watcher.Dispose();
        return 0;
    }
    default:
        Console.Error.WriteLine("使い方: vrcserver scan|show|tag|watch（先頭のコメントを参照）");
        return 2;
}
