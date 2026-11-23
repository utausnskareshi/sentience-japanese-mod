/**
 * 「文章の表示」「文章のスクロール表示」「選択肢の表示」の日本語化。
 *
 * イベントデータ自体は書き換えず、メッセージがゲーム内のメッセージ状態（$gameMessage）に
 * 積まれた直後に、その行だけを訳文に差し替える。こうすることでセーブデータ（実行中の
 * イベントリストを含む）に日本語が混入せず、パッチの適用・解除をまたいでもセーブが使える。
 */
import { containsJapanese, getPrototype, isMethod, isRecord, reportError, wrapMethod, type Method } from '../env.ts';
import { translateChoice, translateMessage } from '../translator.ts';
import { wrapMessage } from '../measure.ts';
import { isJapanese } from '../state.ts';

/**
 * 現在 $gameMessage に積まれている行を訳文に差し替える。
 *
 * @param mode 'message'（文章の表示）または 'scroll'（スクロール文章）
 */
const replaceMessageTexts = (mode: 'message' | 'scroll'): void => {
  const lines = $gameMessage._texts;
  if (lines.length === 0) {
    return;
  }
  const translated = translateMessage(lines);
  if (translated === undefined) {
    return;
  }
  const isPop = translated.parts.prefix !== '';
  const hasFace = mode === 'message' && $gameMessage.faceName() !== '';
  const wrapped = wrapMessage(translated.text, isPop, hasFace);
  // 先頭行に吹き出し指定（\pop[..]）を付け直す
  const first = wrapped[0] ?? '';
  $gameMessage._texts = [`${translated.parts.prefix}${first}`, ...wrapped.slice(1)];
};

/**
 * コマンド処理（command101 / command105）の後に訳文へ差し替えるラッパーを作る。
 */
const afterCommand =
  (mode: 'message' | 'scroll') =>
  (original: Method): Method =>
    function (this: unknown, ...args: unknown[]): unknown {
      // 表示中のメッセージがある場合、元の処理は何も積まずに待機するだけなので差し替えない
      const wasBusy = $gameMessage.isBusy();
      const result = original.apply(this, args);
      if (!wasBusy && isJapanese()) {
        try {
          replaceMessageTexts(mode);
        } catch (error) {
          reportError('メッセージの翻訳', error);
        }
      }
      return result;
    };

/** 選択肢の翻訳ラッパー */
const translateChoices =
  (original: Method): Method =>
  function (this: unknown, ...args: unknown[]): unknown {
    const [choices, ...rest] = args;
    if (!Array.isArray(choices) || !isJapanese()) {
      return original.apply(this, args);
    }
    // 訳文の準備だけを try で囲む（元の処理の例外を翻訳の失敗と取り違えて二重に実行しないため）
    let translated: unknown[] = choices;
    try {
      translated = choices.map((c: unknown) => (typeof c === 'string' && !containsJapanese(c) ? translateChoice(c) : c));
    } catch (error) {
      reportError('選択肢の翻訳', error);
      translated = choices;
    }
    return original.apply(this, [translated, ...rest]);
  };

/**
 * 改ページ判定の補正（吹き出しでない通常ウィンドウ用）。
 *
 * GALV_MessageStyles は Window_Message の内容ビットマップを画面の高さで作るため、
 * 標準の判定（内容ビットマップの高さと比較）では 4 行を超えても改ページされず、
 * 5 行目以降が窓の外に描かれて見えなくなる。吹き出しでないときは、見えている
 * 内側の高さで判定し直して、標準どおり改ページ（入力待ち）させる。
 */
const pageBreakByVisibleHeight =
  (original: Method): Method =>
  function (this: unknown, ...args: unknown[]): unknown {
    const result = original.apply(this, args);
    if (result === true || !isRecord(this)) {
      return result;
    }
    try {
      // 吹き出し表示中（GALV の pTarget がある）は窓が文章に合わせて伸びるので補正しない
      if (this['pTarget'] !== null && this['pTarget'] !== undefined) {
        return result;
      }
      const textState = args[0];
      const height = this['height'];
      const standardPadding = this['standardPadding'];
      if (!isRecord(textState) || typeof height !== 'number' || !isMethod(standardPadding)) {
        return result;
      }
      const { y, height: lineHeight, index, text } = textState;
      if (typeof y !== 'number' || typeof lineHeight !== 'number') {
        return result;
      }
      const isEndOfText = typeof index === 'number' && typeof text === 'string' && index >= text.length;
      const padding = standardPadding.call(this);
      const visibleHeight = height - (typeof padding === 'number' ? padding : 18) * 2;
      return !isEndOfText && y + lineHeight > visibleHeight;
    } catch (error) {
      reportError('改ページの判定', error);
      return result;
    }
  };

/** フックを差し込む */
export const installMessageHooks = (): void => {
  const interpreter = Game_Interpreter.prototype as unknown as Record<string, unknown>;
  wrapMethod(interpreter, 'command101', afterCommand('message'), 'Game_Interpreter.prototype');
  wrapMethod(interpreter, 'command105', afterCommand('scroll'), 'Game_Interpreter.prototype');
  wrapMethod(Game_Message.prototype as unknown as Record<string, unknown>, 'setChoices', translateChoices, 'Game_Message.prototype');
  wrapMethod(getPrototype('Window_Message'), 'needsNewPage', pageBreakByVisibleHeight, 'Window_Message.prototype');
};
