/**
 * マップ名の窓（Window_MapName）の幅の調整（LT-04）。
 *
 * 標準のマップ名の窓は幅 360（文字の枠 324px）で、表示名を枠の幅を最大幅とする drawText で描くため、
 * 枠より長い名前は横に圧縮して描かれる。日本語の表示名には 12〜13 字（文字サイズ 28 で 336〜364px）のものがあり、
 * 0.89〜0.96 倍に縮んでいた（元の英語版も 47 件中 9 件が枠を超え、最大 0.66 倍に縮む）。
 * 訳語を短くする案は、このゲームのフォントの半角の空白や記号が広い（空白 18.6px・& 25.8px）ため収まらないものが残る。
 * そこで日本語表示のときだけ、窓の幅を表示名が収まるところまで広げる（元の幅より狭くはしない。英語表示のときは元のまま）。
 *
 * 窓は Scene_Map を作るたびに（場所移動のたびに）作り直され、作成時の幅の計算（windowWidth）の時点で
 * $gameMap.displayName() は移動先のマップの表示名（data.ts で訳文に差し替え済み）になっている。
 * 窓は画面の左上に置かれるため、広げても右へ伸びるだけで、他の表示の位置は変わらない。
 */
import { getGlobalPath, getPrototype, isMethod, isRecord, reportError, wrapMethod, type Method } from '../env.ts';
import { createMeasure } from '../measure.ts';
import { isJapanese } from '../state.ts';

/** 現在のマップの表示名（取れなければ空文字） */
const currentDisplayName = (): string => {
  const map = getGlobalPath('$gameMap');
  if (!isRecord(map) || !isMethod(map['displayName'])) {
    return '';
  }
  const name = map['displayName'].call(map);
  return typeof name === 'string' ? name : '';
};

/**
 * 窓の幅の計算を包み、日本語表示のときは表示名が文字の枠に収まる幅にする。
 * 必要な幅 = 表示名の幅（切り上げ）＋ 窓の余白×2 ＋ 文字の余白（textPadding）×2（左右に少し間を空ける）。
 */
const fitToDisplayName =
  (original: Method): Method =>
  function (this: unknown, ...args: unknown[]): unknown {
    const width = original.apply(this, args);
    if (typeof width !== 'number' || !isJapanese() || !(this instanceof Window_Base)) {
      return width;
    }
    try {
      const name = currentDisplayName();
      if (name === '') {
        return width;
      }
      // 描画と同じフォント（窓の既定の文字サイズ・フォント名）で測る。日本語の部分は font.ts が GameFont に合成した日本語フォントで測られる
      const textWidth = createMeasure(this.standardFontSize(), this.standardFontFace())(name);
      const needed = Math.ceil(textWidth) + this.standardPadding() * 2 + this.textPadding() * 2;
      return Math.max(width, needed);
    } catch (error) {
      reportError('マップ名の窓の幅の調整', error);
      return width;
    }
  };

/** フックを差し込む */
export const installMapNameHooks = (): void => {
  wrapMethod(getPrototype('Window_MapName'), 'windowWidth', fitToDisplayName, 'Window_MapName.prototype');
};
