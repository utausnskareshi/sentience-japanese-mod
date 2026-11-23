/**
 * Sentience: The Android's Tale 非公式日本語化プラグインのエントリポイント。
 *
 * パッチツールが www/js/plugins/SentienceJP.js として配置し、plugins.js の末尾に登録する。
 * 他のすべてのプラグインの後に読み込まれるため、各プラグインの処理を包む形で日本語化する。
 *
 * 方針:
 * - ゲームのデータファイル・画像・音声は一切書き換えない（実行中のメモリ上でのみ差し替える）
 * - 会話などセーブに残りうる文章は表示直前に翻訳し、セーブデータに日本語を書き込まない
 * - 訳が無い文章や、想定外の状況では英語のまま表示を続ける（ゲームを止めない）
 */
import { loadBundle, bundleVersion, translationStats, setDebug } from './translator.ts';
import { reportError, reportedErrorCount, warn } from './env.ts';
import { isJapanese, setJapanese, swapCount } from './state.ts';
import { installFontHooks } from './hooks/font.ts';
import { installMessageHooks } from './hooks/messages.ts';
import { installDataHooks } from './hooks/data.ts';
import { installActorHooks } from './hooks/actors.ts';
import { installQuestHooks } from './hooks/quests.ts';
import { installMenuHooks } from './hooks/menus.ts';
import { installOptionHooks } from './hooks/options.ts';
import { installOverlayHooks } from './hooks/overlays.ts';
import { installMapNameHooks } from './hooks/mapname.ts';

/** プラグインのバージョン（ビルド時に埋め込む） */
declare const __PLUGIN_VERSION__: string;

/** コンソールから状態を確認するためのデバッグ用 API */
interface DebugApi {
  readonly version: string;
  readonly translationVersion: () => string;
  readonly stats: () => unknown;
  readonly setJapanese: (value: boolean) => void;
  readonly setDebug: (value: boolean) => void;
}

/** 各フックを順に差し込む（1 つが失敗しても他は続ける） */
const install = (): void => {
  const steps: ReadonlyArray<readonly [string, () => void]> = [
    ['日本語フォントの設定', installFontHooks],
    ['会話・選択肢の日本語化', installMessageHooks],
    ['データベース・用語の日本語化', installDataHooks],
    ['アクター名の日本語化', installActorHooks],
    ['ミッションログの日本語化', installQuestHooks],
    ['メニューの日本語化', installMenuHooks],
    ['言語切替オプションの追加', installOptionHooks],
    ['画像内の文字の日本語化', installOverlayHooks],
    ['マップ名の窓の幅の調整', installMapNameHooks],
  ];
  steps.forEach(([label, run]) => {
    try {
      run();
    } catch (error) {
      reportError(label, error);
    }
  });
};

/**
 * 読み込み済みの印（window のプロパティ名。他プラグインと衝突しない名前にする）。
 * plugins.js の手作業の編集などで本プラグインが 2 回登録されると、すべてのフックが二重に包まれ、
 * オプションの「言語」行が 2 つになるなどの不具合が出るため、2 回目以降の読み込みでは何もしない（RA-06）。
 */
const LOADED_MARK = '__sentienceJpLoaded';

/** 起動処理 */
const boot = (): void => {
  const globalObject = window as unknown as Record<string, unknown>;
  if (globalObject[LOADED_MARK] === true) {
    warn('日本語化プラグインが二重に読み込まれたため、2 回目の読み込みでは何もしません（plugins.js の登録が重複していないか確認してください）');
    return;
  }
  globalObject[LOADED_MARK] = true;
  try {
    loadBundle();
  } catch (error) {
    // 訳文が読めない場合は日本語化を行わず、英語のままゲームを続ける
    reportError('訳文データの読み込み', error);
    return;
  }
  install();
  const api: DebugApi = {
    version: __PLUGIN_VERSION__,
    translationVersion: bundleVersion,
    stats: () => ({ ...translationStats(), japanese: isJapanese(), swaps: swapCount(), errors: reportedErrorCount() }),
    setJapanese,
    setDebug,
  };
  globalObject['SentienceJP'] = api;
  console.log(`[SentienceJP] 日本語化プラグイン v${__PLUGIN_VERSION__}（訳文 ${bundleVersion()}）を読み込みました`);
};

boot();
