/**
 * オプション画面に「言語 / Language」の切替を追加する。
 *
 * 設定は config.rpgsave（ツクールMV標準の設定ファイル）に保存する。
 * 切り替えると、データ側の書き換えを即座に戻し（または再適用し）、以降の表示から反映される。
 */
import { reportError, wrapMethod, type Method } from '../env.ts';
import { isJapanese, setJapanese } from '../state.ts';

/** 設定項目のシンボル（ConfigManager のプロパティ名を兼ねる） */
export const LANGUAGE_SYMBOL = 'sentienceJpLanguage';

/** オプション画面での項目名 */
const LANGUAGE_LABEL = '言語 / Language';

/**
 * オプション画面のウィンドウを広げる幅（ピクセル）。
 * 項目名「言語 / Language」は標準の幅（400）では入りきらず、横に圧縮されて値（English）と接してしまう。
 * 文字サイズ 28 で項目名は約 262、標準の項目名欄は 232 のため、40 広げて 272 にする（中央寄せは標準の処理のまま）。
 */
const OPTIONS_EXTRA_WIDTH = 40;

/** フックを差し込む */
export const installOptionHooks = (): void => {
  // ConfigManager.sentienceJpLanguage を言語状態に結び付ける（Window_Options はこのプロパティを読み書きする）
  Object.defineProperty(ConfigManager, LANGUAGE_SYMBOL, {
    get: (): boolean => isJapanese(),
    set: (value: unknown): void => setJapanese(value === true),
    configurable: true,
  });

  const config = ConfigManager as unknown as Record<string, unknown>;
  // 保存: 既存の設定に言語を追加する
  wrapMethod(
    config,
    'makeData',
    (original: Method): Method =>
      function (this: unknown, ...args: unknown[]): unknown {
        const data = original.apply(this, args);
        try {
          if (typeof data === 'object' && data !== null) {
            (data as Record<string, unknown>)[LANGUAGE_SYMBOL] = isJapanese();
          }
        } catch (error) {
          // 設定の保存は起動・終了の流れの中で呼ばれるため、失敗しても止めずに報告だけする
          reportError('言語設定の保存', error);
        }
        return data;
      },
    'ConfigManager',
  );
  // 読み込み: 未設定（初回）なら日本語
  wrapMethod(
    config,
    'applyData',
    (original: Method): Method =>
      function (this: unknown, ...args: unknown[]): unknown {
        const result = original.apply(this, args);
        try {
          const data = args[0];
          if (typeof data === 'object' && data !== null) {
            const saved = (data as Record<string, unknown>)[LANGUAGE_SYMBOL];
            setJapanese(saved !== false);
          }
        } catch (error) {
          // 起動処理（Scene_Boot → ConfigManager.load）を止めないよう、報告して続行する
          reportError('言語設定の読み込み', error);
        }
        return result;
      },
    'ConfigManager',
  );

  const options = Window_Options.prototype as unknown as Record<string, unknown>;
  wrapMethod(
    options,
    'windowWidth',
    (original: Method): Method =>
      function (this: unknown, ...args: unknown[]): unknown {
        const width = original.apply(this, args);
        return typeof width === 'number' ? width + OPTIONS_EXTRA_WIDTH : width;
      },
    'Window_Options.prototype',
  );
  wrapMethod(
    options,
    'addGeneralOptions',
    (original: Method): Method =>
      function (this: unknown, ...args: unknown[]): unknown {
        const result = original.apply(this, args);
        if (this instanceof Window_Options) {
          this.addCommand(LANGUAGE_LABEL, LANGUAGE_SYMBOL);
        }
        return result;
      },
    'Window_Options.prototype',
  );
  wrapMethod(
    options,
    'statusText',
    (original: Method): Method =>
      function (this: unknown, ...args: unknown[]): unknown {
        try {
          const index = args[0];
          if (this instanceof Window_Options && typeof index === 'number' && this.commandSymbol(index) === LANGUAGE_SYMBOL) {
            return isJapanese() ? '日本語' : 'English';
          }
        } catch (error) {
          reportError('オプション画面の表示', error);
        }
        return original.apply(this, args);
      },
    'Window_Options.prototype',
  );
  // 言語を切り替えたら、他の項目名（用語）も描き直す（標準では変更した行だけが再描画される）
  wrapMethod(
    options,
    'changeValue',
    (original: Method): Method =>
      function (this: unknown, ...args: unknown[]): unknown {
        const result = original.apply(this, args);
        if (args[0] === LANGUAGE_SYMBOL && this instanceof Window_Options) {
          try {
            this.refresh();
          } catch (error) {
            reportError('オプション画面の再描画', error);
          }
        }
        return result;
      },
    'Window_Options.prototype',
  );
};
