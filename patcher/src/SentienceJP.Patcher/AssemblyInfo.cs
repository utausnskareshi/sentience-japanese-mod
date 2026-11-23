using System.Windows;

// WPF のテーマ用リソースの場所の指定。
// 1 つ目: テーマ別のリソース辞書の場所（このアプリはテーマ別の辞書を持たないため None）。
//         画面やアプリのリソースで見つからなかった場合に参照される。
// 2 つ目: 汎用のリソース辞書（Themes/Generic.xaml）の場所（このアセンブリ自身）。
//         画面・アプリ・テーマ別のリソースのいずれでも見つからなかった場合に参照される。
[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,
    ResourceDictionaryLocation.SourceAssembly)]
