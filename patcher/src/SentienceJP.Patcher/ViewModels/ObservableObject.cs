using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SentienceJP.Patcher.ViewModels;

/// <summary>
/// プロパティ変更通知の基底クラス（外部ライブラリに依存しない最小実装）。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 値が変わった場合だけフィールドを更新し、変更を通知する。
    /// </summary>
    /// <typeparam name="T">プロパティの型。</typeparam>
    /// <param name="field">更新するフィールド。</param>
    /// <param name="value">新しい値。</param>
    /// <param name="propertyName">プロパティ名（自動で設定される）。</param>
    /// <returns>値が変わった場合は true。</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>変更を通知する。</summary>
    /// <param name="propertyName">プロパティ名。</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
