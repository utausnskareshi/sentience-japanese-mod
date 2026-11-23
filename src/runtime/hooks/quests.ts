/**
 * ミッションログ（GameusQuestSystem）の日本語化。
 *
 * - クエスト本体（名前・説明・手順）はクエスト開始時にセーブデータへ複製されるため、
 *   描画の間だけ一時的に訳文へ差し替え、描画後に必ず元へ戻す。
 * - プラグイン内に直書きされた "Steps" などの見出しは、描画中だけ drawText を経由して翻訳する。
 * - 説明文の折り返し（sliceText）は空白区切り前提のため、日本語用の折り返しに置き換える。
 */
import { containsJapanese, getGlobalPath, getPrototype, isMethod, isRecord, reportError, wrapMethod, type Method } from '../env.ts';
import { plainTranslation, translatePlain } from '../translator.ts';
import { isJapanese, registerSwap } from '../state.ts';
import { wrapForBitmap } from '../measure.ts';

/** クエストの手順（[テキスト, 変数使用, 変数ID, 最大値, 割合表示]） */
type StepList = unknown[];

/** 手順の配列を、テキストだけ訳した新しい配列にする（元の配列は変更しない） */
const translateSteps = (steps: unknown): unknown => {
  if (!Array.isArray(steps)) {
    return steps;
  }
  return steps.map((step: unknown) => {
    if (!Array.isArray(step) || typeof step[0] !== 'string') {
      return step;
    }
    const copy: StepList = step.slice();
    copy[0] = translatePlain('quest', step[0]);
    return copy;
  });
};

/**
 * クエストの表示用フィールドを一時的に訳文へ差し替えて処理を実行し、必ず元に戻す。
 */
const withTranslatedQuest = (quest: unknown, run: () => unknown): unknown => {
  if (!isRecord(quest) || !isJapanese()) {
    return run();
  }
  const backup = { name: quest['name'], desc: quest['desc'], steps: quest['steps'] };
  // 訳文の準備だけを try で囲む。失敗したら原文のまま 1 回だけ描画する
  // （描画処理そのものの例外を翻訳の失敗と取り違えて、描画を二重に実行しないため）
  let translated: { readonly name: unknown; readonly desc: unknown; readonly steps: unknown };
  try {
    translated = {
      name: typeof backup.name === 'string' ? translatePlain('quest', backup.name) : backup.name,
      desc: typeof backup.desc === 'string' ? translatePlain('quest', backup.desc) : backup.desc,
      steps: translateSteps(backup.steps),
    };
  } catch (error) {
    reportError('ミッションログの翻訳', error);
    return run();
  }
  quest['name'] = translated.name;
  quest['desc'] = translated.desc;
  quest['steps'] = translated.steps;
  try {
    return run();
  } finally {
    // 描画が成功しても例外でも、必ず元（英語）に戻す（セーブデータに日本語を残さない）
    quest['name'] = backup.name;
    quest['desc'] = backup.desc;
    quest['steps'] = backup.steps;
  }
};

/**
 * 描画中だけ、ビットマップの drawText に渡る UI 文言（"Steps" など）を翻訳する。
 */
const withUiTextTranslation = (bitmap: unknown, run: () => unknown): unknown => {
  if (!isRecord(bitmap) || !isJapanese()) {
    return run();
  }
  const ownDrawText = Object.prototype.hasOwnProperty.call(bitmap, 'drawText') ? bitmap['drawText'] : undefined;
  const drawText = bitmap['drawText'];
  if (!isMethod(drawText)) {
    return run();
  }
  bitmap['drawText'] = function (this: unknown, ...args: unknown[]): unknown {
    const [text, ...rest] = args;
    const translated = typeof text === 'string' && !containsJapanese(text) ? translatePlain('term', text) : text;
    return drawText.apply(this, [translated, ...rest]);
  };
  try {
    return run();
  } finally {
    // 元がインスタンス固有のメソッドならそれを戻し、そうでなければ上書きを削除して prototype に戻す
    if (ownDrawText === undefined) {
      delete bitmap['drawText'];
    } else {
      bitmap['drawText'] = ownDrawText;
    }
  }
};

/** クエスト詳細ウィンドウの描画メソッドを包むラッパー */
const questDrawer =
  (withUiText: boolean) =>
  (original: Method): Method =>
    function (this: unknown, ...args: unknown[]): unknown {
      const quest = args[0];
      const self = isRecord(this) ? this : {};
      const draw = (): unknown => original.apply(this, args);
      // 元の描画処理の例外はそのまま伝える（プラグインが無い場合と同じ挙動）。
      // 翻訳の準備の失敗は withTranslatedQuest の中で報告し、原文で描画する
      return withTranslatedQuest(quest, () => (withUiText ? withUiTextTranslation(self['questBitmap'], draw) : draw()));
    };

/** クエスト一覧のコマンド名（"  " + クエスト名）を翻訳する */
const translateQuestList =
  (original: Method): Method =>
  function (this: unknown, ...args: unknown[]): unknown {
    const result = original.apply(this, args);
    if (!isJapanese() || !isRecord(this) || !Array.isArray(this['_list'])) {
      return result;
    }
    try {
      (this['_list'] as unknown[]).forEach((command: unknown) => {
        if (isRecord(command) && command['symbol'] === 'quest' && typeof command['name'] === 'string') {
          command['name'] = translatePlain('quest', command['name']);
        }
      });
    } catch (error) {
      reportError('クエスト一覧の翻訳', error);
    }
    return result;
  };

/**
 * 手順・報酬の行頭記号を返す。GameusQuestSystem と同じ式（String(設定の "Bullet Character" || "-") + " "）で作る。
 */
const questBullet = (): string => {
  const config = getGlobalPath('GameusScripts.Config.QuestSystem');
  const value: unknown = isRecord(config) ? config['Bullet Character'] : undefined;
  return `${String(value || '-')} `;
};

/** 日本語を含む文章は日本語用の折り返し、それ以外は元の（空白区切りの）折り返しを使う */
const cjkSliceText =
  (original: Method): Method =>
  function (this: unknown, ...args: unknown[]): unknown {
    const [text, width] = args;
    if (typeof text !== 'string' || typeof width !== 'number' || !containsJapanese(text) || !isRecord(this)) {
      return original.apply(this, args);
    }
    const contents = this['contents'];
    if (!(contents instanceof Bitmap)) {
      return original.apply(this, args);
    }
    try {
      // 手順・報酬（行頭記号で始まる文字列）は、2 行目以降が行頭記号の幅だけ右へ字下げして描かれる
      // （GameusQuestSystem の drawQuestSteps / drawQuestRewards）。幅いっぱいで折り返すと右端が欠けるため、
      // 行頭記号の幅を引いた幅で折り返す（1 行目もわずかに短くなるが、欠けを防ぐことを優先する。RA-05）。
      // 字下げの幅はゲームと同じく contents で測る。
      const bullet = questBullet();
      const indent = text.startsWith(bullet) ? contents.measureTextWidth(bullet) : 0;
      return wrapForBitmap(text, contents, Math.max(1, width - indent));
    } catch (error) {
      reportError('説明文の折り返し', error);
      return original.apply(this, args);
    }
  };

/** プラグインの表示文言（GameusScripts.Config.QuestSystem）を差し替える */
const translateQuestConfig = (): void => {
  const config = getGlobalPath('GameusScripts.Config.QuestSystem');
  if (!isRecord(config)) {
    return;
  }
  ['No Quests Text', 'All Word', 'Completed Word', 'Failed Word', 'In-Progress Word'].forEach((key) => {
    const en = config[key];
    if (typeof en === 'string' && en.trim() !== '') {
      const ja = plainTranslation('term', en);
      if (ja !== undefined) {
        registerSwap('database', en, ja, (value) => {
          config[key] = value;
        });
      }
    }
  });
  // フィルタ窓は "Progress Word" を参照する（未設定時は直書きの "In-Progress"）
  const progress = plainTranslation('term', 'In-Progress');
  if (progress !== undefined) {
    const en = config['Progress Word'];
    registerSwap('database', typeof en === 'string' ? en : undefined, progress, (value) => {
      config['Progress Word'] = value;
    });
  }
};

/** フックを差し込む（GameusQuestSystem が無い場合は警告だけ出して省略） */
export const installQuestHooks = (): void => {
  const info = getPrototype('Window_QuestInfo');
  wrapMethod(info, 'drawQuestInfo', questDrawer(false), 'Window_QuestInfo.prototype');
  wrapMethod(info, 'drawQuestSteps', questDrawer(true), 'Window_QuestInfo.prototype');
  wrapMethod(info, 'drawQuestRewards', questDrawer(true), 'Window_QuestInfo.prototype');
  wrapMethod(getPrototype('Window_Quests'), 'makeCommandList', translateQuestList, 'Window_Quests.prototype');
  wrapMethod(getPrototype('Window_Base'), 'sliceText', cjkSliceText, 'Window_Base.prototype');
  translateQuestConfig();
};
