using FaraVrcPhotoExtension;

// 手元の確認用 CLI（常駐ソフト本体は E3 で作る）。
//   vrcext scan --name <自分の VRChat の表示名> [--logs <ログのフォルダ>] [--history <history.jsonl>]   ログを読んで履歴に追記する
//   vrcext show [--history <history.jsonl>] [--at "yyyy-MM-dd HH:mm:ss"]                               履歴を表示する（--at で、その時刻のアバター）
// 表示名は、履歴・出力のどちらにも出さない（ログから自分の行を選ぶためにだけ使う）。

var values = new Dictionary<string, string>(StringComparer.Ordinal);
var positional = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length) values[args[i][2..]] = args[++i];
    else positional.Add(args[i]);
}

var history = new HistoryStore(values.GetValueOrDefault("history") ?? HistoryStore.DefaultPath);

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
    default:
        Console.Error.WriteLine("使い方: vrcext scan --name <表示名> [--logs <フォルダ>] [--history <ファイル>] | vrcext show [--history <ファイル>] [--at \"yyyy-MM-dd HH:mm:ss\"]");
        return 2;
}
