/**
 * メインメニュー（YEP_MainMenuManager）と人物・生物データベース画面（YEP_CommonEventMenu）の日本語化。
 */
import { containsJapanese, getGlobalPath, getPrototype, isRecord, reportError, wrapMethod, type Method } from '../env.ts';
import { plainTranslation } from '../translator.ts';
import { registerSwap } from '../state.ts';
import { wrapForBitmap } from '../measure.ts';

/**
 * YEP_MainMenuManager のメニュー名を差し替える。
 * メニュー名は JavaScript の式として保持されている（例: "'Area Map'"）ため、
 * 文字列リテラルのものだけを対象に、訳文の文字列リテラルへ置き換える。
 */
const translateMainMenuNames = (): void => {
  const names = getGlobalPath('Yanfly.MMM.Name');
  if (!isRecord(names)) {
    return;
  }
  Object.keys(names).forEach((index) => {
    const expression = names[index];
    if (typeof expression !== 'string') {
      return;
    }
    const literal = /^\s*(['"])(.*)\1\s*$/.exec(expression);
    if (literal === null) {
      return;
    }
    const ja = plainTranslation('term', literal[2] ?? '');
    if (ja !== undefined) {
      // JSON.stringify で安全な文字列リテラルにする（引用符やバックスラッシュをエスケープ）
      registerSwap('database', expression, JSON.stringify(ja), (value) => {
        names[index] = value;
      });
    }
  });
};

/**
 * データベース画面の説明文（Window_Help）を、ウィンドウ幅に合わせて日本語用に折り返す。
 * 英語の説明文は元から手作業で改行されているため、日本語のときだけ処理する。
 */
const wrapSubtext =
  (original: Method): Method =>
  function (this: unknown, ...args: unknown[]): unknown {
    // 準備: 折り返した説明文を先に作り、元の処理の間だけ差し替える（描画は元の処理の 1 回だけで済む）。
    // 準備に失敗したら、原文のまま元の処理を 1 回だけ実行する。
    let swap: { readonly settings: Record<string, unknown>; readonly original: string; readonly wrapped: string } | undefined;
    try {
      const item = args[0];
      const win = isRecord(this) ? this['_subtextWindow'] : undefined;
      const settings = isRecord(item) ? item['menuSettings'] : undefined;
      const subtext = isRecord(settings) ? settings['subtext'] : undefined;
      if (win instanceof Window_Help && isRecord(settings) && typeof subtext === 'string' && containsJapanese(subtext)) {
        // 元の処理と同じく、既定のフォント設定に戻してから幅を測る
        win.resetFontSettings();
        const maxWidth = win.contentsWidth() - win.textPadding() * 2;
        swap = { settings, original: subtext, wrapped: wrapForBitmap(subtext, win.contents, maxWidth).join('\n') };
      }
    } catch (error) {
      reportError('データベース画面の説明文の折り返し', error);
      swap = undefined;
    }
    if (swap === undefined) {
      return original.apply(this, args);
    }
    swap.settings['subtext'] = swap.wrapped;
    try {
      return original.apply(this, args);
    } finally {
      // データ（言語切替の記録の対象）は必ず元に戻す
      swap.settings['subtext'] = swap.original;
    }
  };

/** フックを差し込む */
export const installMenuHooks = (): void => {
  translateMainMenuNames();
  wrapMethod(getPrototype('Window_CommonEventMenu'), 'setSubtextWindowItem', wrapSubtext, 'Window_CommonEventMenu.prototype');
};
