/**
 * 日本語フォントの設定。
 *
 * ゲームの文字は "GameFont"（英字用の Anita）で描画される。そこへ、日本語の文字範囲だけを
 * 担当する @font-face を同じ "GameFont" 名で追加する（unicode-range による合成フォント）。
 * 英字は元の Anita のまま、かな・漢字だけがゲーム同梱の M+ 1m で表示される。
 * フォントファイルはゲーム本体に含まれる www/fonts/mplus-1m-regular.ttf を使い、配布物には含めない。
 */
import { reportError, warn, wrapMethod, type Method } from '../env.ts';

/** 日本語用フォントファイル（www からの相対パス。ゲーム同梱） */
const JAPANESE_FONT_URL = 'fonts/mplus-1m-regular.ttf';

/**
 * 日本語フォントが担当する文字範囲。
 * 句読点・かぎ括弧・全角英数・かな・漢字・記号類（…や―など）を含める。
 */
const UNICODE_RANGE = [
  'U+2010-2027', // ダッシュ・引用符・…
  'U+2030-203B', // ‰ ※ など
  'U+2190-21FF', // 矢印
  'U+2460-24FF', // 丸数字
  'U+2500-257F', // 罫線
  'U+25A0-25FF', // ■□▲△○● など
  'U+2600-26FF', // ★☆ など
  'U+3000-303F', // 和文の句読点・括弧
  'U+3040-30FF', // ひらがな・カタカナ
  'U+3400-4DBF', // 漢字（拡張A）
  'U+4E00-9FFF', // 漢字
  'U+F900-FAFF', // 互換漢字
  'U+FF00-FFEF', // 全角英数・半角カナ
].join(', ');

/** 先読みの最大待ち時間（ミリ秒）。超えたら待たずに起動を続ける */
const PRELOAD_TIMEOUT_MS = 10000;

/**
 * フォントの読み込みに失敗したときの、その後の表示の説明（EA-01）。
 * 失敗しても日本語化は続き、かな・漢字は Windows の代替フォントで表示されるため、
 * reportError の既定の「原文で表示を続けます」では実際の挙動と食い違う。
 */
const FONT_FALLBACK_NOTE = '日本語は代替フォントで表示します';

/** 先読みが完了（または打ち切り）したか */
let ready = false;

/** @font-face を追加する */
const injectFontFace = (): void => {
  const style = document.createElement('style');
  style.type = 'text/css';
  style.textContent =
    `@font-face { font-family: "GameFont"; src: url("${JAPANESE_FONT_URL}"); unicode-range: ${UNICODE_RANGE}; }\n` +
    // 合成フォントが効かない環境向けの予備（フォント名の列挙で日本語フォントへフォールバックさせる）
    `@font-face { font-family: "SentienceJP"; src: url("${JAPANESE_FONT_URL}"); }`;
  document.head.appendChild(style);
};

/** 先読みを再試行する間隔（ミリ秒） */
const PRELOAD_RETRY_MS = 200;

/** 確認に使う文字列（かな・漢字） */
const SAMPLE_TEXT = 'あア亜';

/** 日本語の字形を持つフォントが使える状態になったか */
const isJapaneseFontReady = (): boolean =>
  document.fonts.check(`28px GameFont`, SAMPLE_TEXT) && document.fonts.check(`28px SentienceJP`, SAMPLE_TEXT);

/**
 * 日本語フォントを先読みする（Canvas は読み込み完了を待たずに描画するため）。
 * @font-face を追加した直後は、ブラウザがまだ規則を取り込んでおらず load() が空で即完了することがある。
 * そのため完了後に check() で確かめ、未読込なら時間切れまで再試行する。
 * 起動処理（Scene_Boot）は ready を待つので、どの経路でも必ず ready になるようにする
 * （load() が完了しない場合に備えた独立の時間切れ、同期的な例外の捕捉）。
 */
const preload = (): void => {
  const startedAt = Date.now();
  /** 先読みを終える（2 回目以降は何もしない） */
  const finish = (timedOut: boolean): void => {
    if (ready) {
      return;
    }
    if (timedOut) {
      warn('日本語フォントの読み込みが時間内に終わらなかったため、待たずに起動します');
    }
    ready = true;
  };
  // load() の Promise が完了しない場合でも起動を止めないための、独立した時間切れ
  window.setTimeout(() => finish(true), PRELOAD_TIMEOUT_MS);
  const attempt = (): void => {
    if (ready) {
      return;
    }
    try {
      const fonts = document.fonts;
      Promise.all([fonts.load(`28px GameFont`, SAMPLE_TEXT), fonts.load(`28px SentienceJP`, SAMPLE_TEXT)])
        .then(() => {
          if (isJapaneseFontReady()) {
            finish(false);
            return;
          }
          if (Date.now() - startedAt >= PRELOAD_TIMEOUT_MS) {
            finish(true);
            return;
          }
          window.setTimeout(attempt, PRELOAD_RETRY_MS);
        })
        .catch((error: unknown) => {
          // 読み込みに失敗しても起動は止めない（日本語は代替フォントで表示される）
          reportError('日本語フォントの読み込み', error, FONT_FALLBACK_NOTE);
          finish(false);
        });
    } catch (error) {
      // document.fonts が使えない環境などの同期的な例外。起動は止めない
      reportError('日本語フォントの読み込み', error, FONT_FALLBACK_NOTE);
      finish(false);
    }
  };
  attempt();
};

/** 予備のフォント名を既定フォントの列挙に加える（YEP_CoreEngine の既定フォント設定） */
const appendFallbackFamily = (): void => {
  const yanfly = (window as unknown as Record<string, unknown>)['Yanfly'];
  if (typeof yanfly !== 'object' || yanfly === null) {
    return;
  }
  const param = (yanfly as Record<string, unknown>)['Param'];
  if (typeof param !== 'object' || param === null) {
    return;
  }
  const record = param as Record<string, unknown>;
  (['DefaultFont', 'MSGFontName'] as const).forEach((key) => {
    const value = record[key];
    if (typeof value === 'string' && !value.includes('SentienceJP')) {
      // 先頭のフォント（GameFont）の直後に入れて、英字は元のフォントのままにする
      const families = value.split(',').map((f) => f.trim());
      record[key] = [families[0] ?? 'GameFont', 'SentienceJP', ...families.slice(1)].join(', ');
    }
  });
};

/** フォント設定を行い、起動処理（Scene_Boot）が先読み完了を待つようにする */
export const installFontHooks = (): void => {
  injectFontFace();
  appendFallbackFamily();
  preload();
  wrapMethod(
    Scene_Boot.prototype as unknown as Record<string, unknown>,
    'isReady',
    (original: Method): Method =>
      function (this: unknown, ...args: unknown[]): unknown {
        // 元の結果は真値で判定する（他プラグインが true 以外の真値を返しても起動が止まらないように。RA-07）
        return Boolean(original.apply(this, args)) && ready;
      },
    'Scene_Boot.prototype',
  );
};
