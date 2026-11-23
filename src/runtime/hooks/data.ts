/**
 * データベース・システム用語・マップ名・クエストのカテゴリ・人物データベース画面の日本語化。
 *
 * これらはセーブデータに保存されないため、読み込み時にデータを書き換える。
 * 書き換えは state.ts に記録し、オプション画面の言語切替で元に戻せるようにする。
 */
import type { Namespace } from '../../shared/text.ts';
import { getGlobalPath, globals, isRecord, reportError, wrapMethod, type Method } from '../env.ts';
import { blockTranslation, plainTranslation } from '../translator.ts';
import { clearMapSwaps, registerSwap, type SwapScope } from '../state.ts';

/** オブジェクトの文字列プロパティを訳文に差し替える */
const swapField = (scope: SwapScope, ns: Namespace, target: Record<string, unknown>, field: string): void => {
  const en = target[field];
  if (typeof en !== 'string' || en.trim() === '') {
    return;
  }
  const ja = plainTranslation(ns, en);
  if (ja !== undefined) {
    registerSwap(scope, en, ja, (value) => {
      target[field] = value;
    });
  }
};

/** 文字列配列の各要素を訳文に差し替える */
const swapArray = (scope: SwapScope, ns: Namespace, target: unknown): void => {
  if (!Array.isArray(target)) {
    return;
  }
  const array: unknown[] = target;
  array.forEach((en, index) => {
    if (typeof en !== 'string' || en.trim() === '') {
      return;
    }
    const ja = plainTranslation(ns, en);
    if (ja !== undefined) {
      registerSwap(scope, en, ja, (value) => {
        array[index] = value;
      });
    }
  });
};

/** データベース（配列の各要素）の指定フィールドを差し替える */
const swapDatabase = (data: unknown, fields: readonly string[]): void => {
  if (!Array.isArray(data)) {
    return;
  }
  data.forEach((item: unknown) => {
    if (isRecord(item)) {
      fields.forEach((field) => swapField('database', 'db', item, field));
    }
  });
};

/** System.json の用語を差し替える */
const swapSystem = (system: Record<string, unknown>): void => {
  const terms = system['terms'];
  if (isRecord(terms)) {
    ['basic', 'commands', 'params'].forEach((group) => swapArray('database', 'term', terms[group]));
    const messages = terms['messages'];
    if (isRecord(messages)) {
      Object.keys(messages).forEach((key) => swapField('database', 'term', messages, key));
    }
  }
  ['elements', 'skillTypes', 'weaponTypes', 'armorTypes', 'equipTypes'].forEach((group) => swapArray('database', 'term', system[group]));
  swapField('database', 'term', system, 'currencyUnit');
};

/** データベースファイルごとの翻訳対象フィールド（アクターは表示時に翻訳するため含めない） */
const DATABASE_FIELDS: ReadonlyArray<readonly [string, readonly string[]]> = [
  ['$dataClasses', ['name']],
  ['$dataItems', ['name', 'description']],
  ['$dataWeapons', ['name', 'description']],
  ['$dataArmors', ['name', 'description']],
  ['$dataSkills', ['name', 'description', 'message1', 'message2']],
  ['$dataStates', ['name', 'message1', 'message2', 'message3', 'message4']],
  ['$dataEnemies', ['name']],
];

/** 読み込まれたデータの種類を判定して差し替える */
const translateLoadedData = (object: unknown): void => {
  const g = globals();
  if (object === g['$dataMap']) {
    // マップ表示名（マップ移動のたびに読み込まれる）
    clearMapSwaps();
    if (isRecord(object)) {
      swapField('map', 'map', object, 'displayName');
    }
    return;
  }
  if (object === g['$dataSystem'] && isRecord(object)) {
    swapSystem(object);
    return;
  }
  if (object === g['$dataQuests'] && Array.isArray(object)) {
    // GameusQuestSystem: 先頭要素がカテゴリ名の配列。クエスト本体はセーブに複製されるため表示時に翻訳する
    swapArray('database', 'quest', object[0]);
    return;
  }
  const entry = DATABASE_FIELDS.find(([name]) => object === g[name]);
  if (entry !== undefined) {
    swapDatabase(object, entry[1]);
  }
};

/**
 * 人物・生物データベース画面（YEP_CommonEventMenu）の項目名と説明文を差し替える。
 * YEP が注釈を解析した後（isDatabaseLoaded が初めて true を返した時点）に実行する。
 */
const translateCommonEventMenu = (): void => {
  const commonEvents = getGlobalPath('$dataCommonEvents');
  if (!Array.isArray(commonEvents)) {
    return;
  }
  commonEvents.forEach((ce: unknown) => {
    if (!isRecord(ce)) {
      return;
    }
    const settings = ce['menuSettings'];
    if (!isRecord(settings)) {
      return;
    }
    swapField('database', 'cem', settings, 'name');
    const subtext = settings['subtext'];
    if (typeof subtext === 'string' && subtext.trim() !== '') {
      // 説明文は複数行ブロック。既定文言（"Subtext here!"）は UI 文言として登録されている
      const ja = blockTranslation('cem', subtext) ?? plainTranslation('term', subtext);
      if (ja !== undefined) {
        registerSwap('database', subtext, ja, (value) => {
          settings['subtext'] = value;
        });
      }
    }
  });
};

/** フックを差し込む */
export const installDataHooks = (): void => {
  const dataManager = DataManager as unknown as Record<string, unknown>;
  wrapMethod(
    dataManager,
    'onLoad',
    (original: Method): Method =>
      function (this: unknown, ...args: unknown[]): unknown {
        const result = original.apply(this, args);
        try {
          translateLoadedData(args[0]);
        } catch (error) {
          reportError('データの翻訳', error);
        }
        return result;
      },
    'DataManager',
  );

  let menuTranslated = false;
  wrapMethod(
    dataManager,
    'isDatabaseLoaded',
    (original: Method): Method =>
      function (this: unknown, ...args: unknown[]): unknown {
        const loaded = original.apply(this, args);
        // 読み込み済みかは真値で判定する（他プラグインが true 以外の真値を返す場合に備える。RA-07）
        if (Boolean(loaded) && !menuTranslated) {
          menuTranslated = true;
          try {
            translateCommonEventMenu();
          } catch (error) {
            reportError('データベース画面の翻訳', error);
          }
        }
        return loaded;
      },
    'DataManager',
  );
};
