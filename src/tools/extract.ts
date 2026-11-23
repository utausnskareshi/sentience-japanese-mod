/**
 * ゲームのデータから翻訳単位（原文）を抽出する CLI。
 *
 * 使い方:  npm run extract -- [--game <ゲームのフォルダ>]
 * 出力:    work/source/units.json（原文を含むため Git 管理外。公開しないこと）
 *
 * ゲーム内プラグイン（src/runtime）と同じ正規化・キー計算（src/shared）を使うため、
 * ここで作ったキーはゲーム実行時のキーと一致する。
 */
import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { textKey } from '../shared/hash.ts';
import {
  blockBody,
  isBlank,
  listEscapeCodes,
  normalizeEscapeCode,
  splitChoice,
  splitMessageLines,
  splitWhitespace,
  type Namespace,
} from '../shared/text.ts';
import { locateGame } from './lib/game.ts';
import { asArray, asNumber, asRecord, asString, isRecord, readJson, writeJson } from './lib/json.ts';
import { unitId, writeSourceFile, type DisplayKind, type SourceUnit, type UnitRef } from './lib/units.ts';
import { cleanSpriteName, commentsToNotedata, isCodeLikeName, parseCommonEventMenuNotes, parseMenuIds } from './lib/extract-lib.ts';
import { SOURCE_CODES_PATH } from './lib/checks.ts';
import { parseCliArgs } from './lib/cli.ts';

/** 使い方（引数の誤りのときに表示する） */
const USAGE = 'npm run extract -- [--game <ゲームのフォルダ>]';

// ---------------------------------------------------------------------------
// 収集器（このファイル内だけで状態を持つ）
// ---------------------------------------------------------------------------

/** 1 回の出現 */
interface Occurrence {
  readonly ns: Namespace;
  readonly source: string;
  readonly display: DisplayKind;
  readonly ref: UnitRef;
  readonly scene: string;
  readonly sceneTitle: string;
  readonly speaker?: string | undefined;
  /** 顔グラフィック（"MainBase1#3" 形式。話者推定に使う） */
  readonly face?: string | undefined;
}

/** 集計中のユニット */
interface UnitDraft {
  readonly ns: Namespace;
  readonly key: string;
  readonly source: string;
  readonly display: DisplayKind;
  readonly order: number;
  readonly scene: string;
  readonly sceneTitle: string;
  count: number;
  refs: UnitRef[];
  speakers: Map<string, number>;
  faces: Set<string>;
}

/** 出現を集計してユニット一覧を作る収集器を生成する */
const createCollector = () => {
  const drafts = new Map<string, UnitDraft>();
  /** 顔グラフィック → 話者 の出現回数（吹き出しの表示先から推定） */
  const faceSpeakers = new Map<string, Map<string, number>>();
  let order = 0;

  const countUp = (map: Map<string, number>, key: string): void => {
    map.set(key, (map.get(key) ?? 0) + 1);
  };

  const add = (o: Occurrence): void => {
    if (isBlank(o.source)) {
      return;
    }
    const key = textKey(o.source);
    const id = `${o.ns}:${key}`;
    const draft =
      drafts.get(id) ??
      ((): UnitDraft => {
        const created: UnitDraft = {
          ns: o.ns,
          key,
          source: o.source,
          display: o.display,
          order: order++,
          scene: o.scene,
          sceneTitle: o.sceneTitle,
          count: 0,
          refs: [],
          speakers: new Map(),
          faces: new Set(),
        };
        drafts.set(id, created);
        return created;
      })();
    // 異なる原文が同じキーになった場合（ハッシュ衝突）は致命的なので停止する
    if (draft.source !== o.source) {
      throw new Error(`ハッシュ衝突を検出しました: ${id}\n  A: ${draft.source}\n  B: ${o.source}`);
    }
    draft.count += 1;
    if (draft.refs.length < 5) {
      draft.refs.push(o.ref);
    }
    if (o.speaker !== undefined) {
      countUp(draft.speakers, o.speaker);
    }
    if (o.face !== undefined) {
      draft.faces.add(o.face);
      if (o.speaker !== undefined) {
        const map = faceSpeakers.get(o.face) ?? new Map<string, number>();
        countUp(map, o.speaker);
        faceSpeakers.set(o.face, map);
      }
    }
  };

  /** 最頻値を返す */
  const mostFrequent = (map: ReadonlyMap<string, number>): string | undefined =>
    Array.from(map.entries()).sort((a, b) => b[1] - a[1])[0]?.[0];

  /** 集計結果を確定する（顔グラフィックからの話者推定もここで行う） */
  const result = (): SourceUnit[] =>
    Array.from(drafts.values())
      .sort((a, b) => a.order - b.order)
      .map((d): SourceUnit => {
        const direct = mostFrequent(d.speakers);
        const viaFace = Array.from(d.faces)
          .map((f) => {
            const map = faceSpeakers.get(f);
            return map === undefined ? undefined : mostFrequent(map);
          })
          .find((s) => s !== undefined);
        const speaker = direct ?? (viaFace === undefined ? undefined : `${viaFace}?`);
        const base = {
          ns: d.ns,
          key: d.key,
          source: d.source,
          display: d.display,
          order: d.order,
          count: d.count,
          refs: d.refs,
          scene: d.scene,
          sceneTitle: d.sceneTitle,
        };
        return speaker === undefined ? base : { ...base, speaker };
      });

  return { add, result };
};

type Collector = ReturnType<typeof createCollector>;

// ---------------------------------------------------------------------------
// イベントコマンドの解析
// ---------------------------------------------------------------------------

/** イベントコマンド */
interface EventCommand {
  readonly code: number;
  readonly parameters: readonly unknown[];
}

/** イベントコマンドの配列を読み取る */
const readCommands = (list: unknown, where: string): EventCommand[] =>
  asArray(list, where).map((raw, i) => {
    const cmd = asRecord(raw, `${where}[${i}]`);
    return { code: asNumber(cmd['code'], `${where}[${i}].code`), parameters: asArray(cmd['parameters'], `${where}[${i}].parameters`) };
  });

/** 文字列パラメータを取り出す */
const paramString = (cmd: EventCommand, index: number, where: string): string =>
  asString(cmd.parameters[index], `${where}.parameters[${index}]`);

/** イベントリストを解析する際の文脈 */
interface ListContext {
  readonly file: string;
  readonly refPrefix: string;
  readonly scene: string;
  readonly sceneTitle: string;
  /** \pop[..] の表示先から話者名を推定する */
  readonly resolvePopTarget: (target: string) => string | undefined;
  /** 「コモンイベントメニューに追加」で参照されたコモンイベントIDを記録する */
  readonly onMenuCommonEvents: (ids: readonly number[]) => void;
}

/** \pop[x] / \pop[x,y] から表示先を取り出す（座標指定は対象外） */
const parsePopTarget = (prefix: string): string | undefined => {
  const match = /\\pop\[([^\],]*)(,[^\]]*)?\]/i.exec(prefix);
  if (match === null || match[2] !== undefined) {
    return undefined;
  }
  return match[1];
};

/** 続く同種コマンド（401/405）の文字列を集める */
const collectFollowing = (cmds: readonly EventCommand[], start: number, code: number, where: string): string[] => {
  const lines: string[] = [];
  for (let j = start; j < cmds.length && cmds[j]?.code === code; j++) {
    const cmd = cmds[j];
    if (cmd !== undefined) {
      lines.push(paramString(cmd, 0, `${where}#${j}`));
    }
  }
  return lines;
};

/** イベントコマンドの配列から翻訳単位を集める */
const processList = (list: unknown, ctx: ListContext, collector: Collector): void => {
  const where = `${ctx.file} ${ctx.refPrefix}`;
  const cmds = readCommands(list, where);
  const base = { scene: ctx.scene, sceneTitle: ctx.sceneTitle };
  cmds.forEach((cmd, i) => {
    const ref: UnitRef = { file: ctx.file, where: `${ctx.refPrefix} #${i}` };
    switch (cmd.code) {
      case 101: {
        // 文章の表示（顔・背景・位置）＋ 続く 401 が本文
        const lines = collectFollowing(cmds, i + 1, 401, where);
        const parts = splitMessageLines(lines);
        const faceName = paramString(cmd, 0, `${where}#${i}`);
        const faceIndex = asNumber(cmd.parameters[1], `${where}#${i}.parameters[1]`);
        const target = parsePopTarget(parts.prefix);
        collector.add({
          ...base,
          ns: 'msg',
          source: parts.body,
          display: parts.prefix === '' ? 'window' : 'pop',
          ref,
          speaker: target === undefined ? undefined : ctx.resolvePopTarget(target),
          face: faceName === '' ? undefined : `${faceName}#${faceIndex}`,
        });
        break;
      }
      case 105: {
        // 文章のスクロール表示 ＋ 続く 405 が本文
        const lines = collectFollowing(cmds, i + 1, 405, where);
        collector.add({ ...base, ns: 'msg', source: splitMessageLines(lines).body, display: 'scroll', ref });
        break;
      }
      case 102: {
        // 選択肢（各項目を個別に翻訳）
        asArray(cmd.parameters[0], `${where}#${i}.parameters[0]`).forEach((choice, k) => {
          collector.add({
            ...base,
            ns: 'choice',
            source: splitChoice(asString(choice, `${where}#${i}.choices[${k}]`)).body,
            display: 'choice',
            ref,
          });
        });
        break;
      }
      case 320:
      case 324:
      case 325: {
        // 名前・二つ名・プロフィールの変更
        const text = paramString(cmd, 1, `${where}#${i}`);
        if (!isCodeLikeName(text)) {
          collector.add({ ...base, ns: 'name', source: splitWhitespace(text).body, display: 'name', ref });
        }
        break;
      }
      case 356: {
        // プラグインコマンド: コモンイベントメニューへの追加を記録する
        const text = paramString(cmd, 0, `${where}#${i}`);
        const match = /^AddCommonEventMenu\s+(.*)$/i.exec(text);
        if (match !== null) {
          ctx.onMenuCommonEvents(parseMenuIds(match[1] ?? ''));
        }
        break;
      }
      default:
        break;
    }
  });
};

// ---------------------------------------------------------------------------
// 各データファイルの処理
// ---------------------------------------------------------------------------

/** アクター名の一覧（ID → 名前） */
type ActorNames = ReadonlyMap<number, string>;

/** \pop の表示先を話者名に変換する共通処理 */
const resolveCommonTarget = (target: string, actors: ActorNames): string | undefined => {
  if (target === '-1') {
    return '主人公（アンドロイド）';
  }
  if (/^-\d+$/.test(target)) {
    return '同行者';
  }
  const actor = /^a(\d+)$/i.exec(target);
  if (actor !== null) {
    const id = Number(actor[1]);
    const name = actors.get(id) ?? '';
    return id === 1 || name === '' ? '主人公（アンドロイド）' : name;
  }
  return undefined;
};

/** マップファイルを処理する */
const processMap = (
  file: string,
  mapJson: unknown,
  mapTitle: string,
  actors: ActorNames,
  collector: Collector,
  onMenuCommonEvents: (ids: readonly number[]) => void,
): void => {
  const map = asRecord(mapJson, file);
  const displayName = typeof map['displayName'] === 'string' ? map['displayName'] : '';
  if (!isBlank(displayName)) {
    collector.add({
      ns: 'map',
      source: splitWhitespace(displayName).body,
      display: 'map',
      ref: { file, where: 'displayName' },
      scene: 'map-names',
      sceneTitle: 'マップ表示名',
    });
  }
  const events = asArray(map['events'], `${file}.events`).filter(isRecord);
  // イベントID → 歩行グラフィック名（話者推定用）
  const sprites = new Map<number, string>();
  events.forEach((ev) => {
    const id = asNumber(ev['id'], `${file}.event.id`);
    const pages = asArray(ev['pages'], `${file} EV${id}.pages`).filter(isRecord);
    const named = pages
      .map((pg) => (isRecord(pg['image']) ? pg['image']['characterName'] : undefined))
      .find((n): n is string => typeof n === 'string' && n !== '');
    if (named !== undefined) {
      sprites.set(id, cleanSpriteName(named));
    }
  });
  events.forEach((ev) => {
    const id = asNumber(ev['id'], `${file}.event.id`);
    const name = typeof ev['name'] === 'string' ? ev['name'] : '';
    const pages = asArray(ev['pages'], `${file} EV${id}.pages`);
    pages.forEach((pg, p) => {
      const page = asRecord(pg, `${file} EV${id} p${p}`);
      processList(
        page['list'],
        {
          file,
          refPrefix: `EV${id} p${p}`,
          scene: `${file}#EV${id}`,
          sceneTitle: `${mapTitle} / EV${String(id).padStart(3, '0')} ${name}`,
          resolvePopTarget: (target) => {
            const common = resolveCommonTarget(target, actors);
            if (common !== undefined) {
              return common;
            }
            const num = Number(target);
            if (!Number.isInteger(num)) {
              return undefined;
            }
            return sprites.get(num === 0 ? id : num);
          },
          onMenuCommonEvents,
        },
        collector,
      );
    });
  });
};

/** データベースの各項目の文字列フィールドを集める */
const processDatabase = (
  file: string,
  json: unknown,
  fields: readonly string[],
  ns: Namespace,
  display: DisplayKind,
  collector: Collector,
): void => {
  asArray(json, file).forEach((raw, i) => {
    if (!isRecord(raw)) {
      return;
    }
    fields.forEach((field) => {
      const value = raw[field];
      if (typeof value === 'string' && !isBlank(value) && !isCodeLikeName(value)) {
        collector.add({
          ns,
          source: splitWhitespace(value).body,
          display,
          ref: { file, where: `[${i}].${field}` },
          scene: `db:${file}`,
          sceneTitle: `データベース ${file}`,
        });
      }
    });
  });
};

/** System.json の用語を集める */
const processSystem = (json: unknown, collector: Collector): void => {
  const file = 'System.json';
  const sys = asRecord(json, file);
  const addTerm = (value: unknown, where: string): void => {
    if (typeof value === 'string' && !isBlank(value)) {
      collector.add({
        ns: 'term',
        source: splitWhitespace(value).body,
        display: 'term',
        ref: { file, where },
        scene: 'terms',
        sceneTitle: 'システム用語',
      });
    }
  };
  const terms = asRecord(sys['terms'], `${file}.terms`);
  for (const group of ['basic', 'commands', 'params']) {
    asArray(terms[group], `${file}.terms.${group}`).forEach((v, i) => addTerm(v, `terms.${group}[${i}]`));
  }
  const messages = asRecord(terms['messages'], `${file}.terms.messages`);
  Object.keys(messages).forEach((k) => addTerm(messages[k], `terms.messages.${k}`));
  for (const group of ['elements', 'skillTypes', 'weaponTypes', 'armorTypes', 'equipTypes']) {
    asArray(sys[group], `${file}.${group}`).forEach((v, i) => addTerm(v, `${group}[${i}]`));
  }
  addTerm(sys['currencyUnit'], 'currencyUnit');
};

/** Quests.json（GameusQuestSystem）を処理する */
const processQuests = (json: unknown, collector: Collector): void => {
  const file = 'Quests.json';
  const all = asArray(json, file);
  const add = (value: unknown, where: string): void => {
    if (typeof value === 'string' && !isBlank(value)) {
      collector.add({
        ns: 'quest',
        source: splitWhitespace(value).body,
        display: 'quest',
        ref: { file, where },
        scene: 'quests',
        sceneTitle: 'ミッションログ（クエスト）',
      });
    }
  };
  // 先頭要素はカテゴリ名の配列
  asArray(all[0], `${file}[0]`).forEach((v, i) => add(v, `[0][${i}]`));
  all.slice(1).forEach((raw, idx) => {
    if (!isRecord(raw)) {
      return;
    }
    const i = idx + 1;
    add(raw['name'], `[${i}].name`);
    add(raw['desc'], `[${i}].desc`);
    asArray(raw['steps'] ?? [], `${file}[${i}].steps`).forEach((step, s) => {
      add(asArray(step, `${file}[${i}].steps[${s}]`)[0], `[${i}].steps[${s}][0]`);
    });
    // 報酬（rewards）は抽出しない。本作では報酬の描画（drawQuestRewards）が呼ばれず表示されないうえ、
    // 配列の先頭は "item" などの種別の識別子で、翻訳すべき文章ではないため。
  });
};

/** コモンイベントメニューの項目名・説明文を集める（YEP_CommonEventMenu と同じ解析） */
const processCommonEventMenu = (commonEvents: readonly unknown[], ids: ReadonlySet<number>, collector: Collector): void => {
  const file = 'CommonEvents.json';
  Array.from(ids)
    .sort((a, b) => a - b)
    .forEach((id) => {
      const raw = commonEvents[id];
      if (!isRecord(raw)) {
        return;
      }
      const name = typeof raw['name'] === 'string' ? raw['name'] : '';
      const list = readCommands(raw['list'], `${file}[${id}].list`);
      // YEP_CommonEventMenu と同じ手順で注釈を解析する（extract-lib.ts。単体テストあり）
      const comments = list.filter((c) => c.code === 108 || c.code === 408).map((c) => String(c.parameters[0]));
      const { menuName, subtext } = parseCommonEventMenuNotes(commentsToNotedata(comments), name);
      const scene = `cem#${id}`;
      const sceneTitle = `データベース画面 CE${String(id).padStart(3, '0')} ${name}`;
      if (!isBlank(menuName)) {
        collector.add({ ns: 'cem', source: splitWhitespace(menuName).body, display: 'menu', ref: { file, where: `[${id}].name` }, scene, sceneTitle });
      }
      if (subtext !== undefined && !isBlank(subtext)) {
        // 本文はゲーム実行時の blockKey と同じ blockBody で作る（同じ関数なのでキーは必ず一致する）
        collector.add({ ns: 'cem', source: blockBody(subtext), display: 'subtext', ref: { file, where: `[${id}].<Subtext>` }, scene, sceneTitle });
      }
    });
};

/** plugins.js のパラメータとハードコード文言を集める */
const processPlugins = (www: string, collector: Collector): void => {
  const file = 'plugins.js';
  const src = readFileSync(join(www, 'js', 'plugins.js'), 'utf8');
  const match = /\$plugins\s*=\s*(\[[\s\S]*\])\s*;?\s*$/.exec(src);
  if (match === null) {
    throw new Error('plugins.js から $plugins 配列を見つけられません');
  }
  const plugins = asArray(JSON.parse(match[1] ?? '[]') as unknown, file).filter(isRecord);
  const add = (text: string, where: string, display: DisplayKind): void => {
    if (!isBlank(text)) {
      collector.add({ ns: 'term', source: splitWhitespace(text).body, display, ref: { file, where }, scene: 'ui', sceneTitle: 'メニュー・UI文言' });
    }
  };
  const params = (name: string): Record<string, unknown> => {
    const plugin = plugins.find((p) => p['name'] === name);
    return plugin !== undefined && isRecord(plugin['parameters']) ? plugin['parameters'] : {};
  };
  // YEP_MainMenuManager: 'Area Map' のような文字列リテラルのメニュー名だけが対象
  const mmm = params('YEP_MainMenuManager');
  Object.keys(mmm)
    .filter((k) => /^Menu \d+ Name$/.test(k))
    .forEach((k) => {
      const literal = /^\s*(['"])(.*)\1\s*$/.exec(String(mmm[k]));
      if (literal !== null) {
        add(literal[2] ?? '', `YEP_MainMenuManager.${k}`, 'menu');
      }
    });
  // GameusQuestSystem の表示文言
  const quest = params('GameusQuestSystem');
  ['No Quests Text', 'All Word', 'Completed Word', 'Failed Word', 'In-Progress Word', 'Steps Word', 'Rewards Word', 'Hidden Reward Text'].forEach((k) => {
    const v = quest[k];
    if (typeof v === 'string' && /[a-z]/i.test(v)) {
      add(v, `GameusQuestSystem.${k}`, 'term');
    }
  });
  // YEP_CommonEventMenu の既定文言（説明文が無い項目で表示される）
  const cem = params('YEP_CommonEventMenu');
  ['Default Help', 'Default Subtext'].forEach((k) => {
    const v = cem[k];
    if (typeof v === 'string') {
      add(v, `YEP_CommonEventMenu.${k}`, 'term');
    }
  });
  // プラグイン内にハードコードされている文言（ゲーム内プラグインで差し替える）
  ['Steps', 'Rewards', 'In-Progress', 'No Quests', 'All', 'Completed', 'Failed'].forEach((text) =>
    add(text, '(hardcoded) GameusQuestSystem', 'term'),
  );
};

// ---------------------------------------------------------------------------
// メイン
// ---------------------------------------------------------------------------

const main = (): void => {
  const { values } = parseCliArgs(process.argv.slice(2), { usage: USAGE, options: { game: 'string' }, allowPositionals: false });
  const game = locateGame(values.game);
  const dataDir = join(game.www, 'data');
  const collector = createCollector();
  const menuIds = new Set<number>();
  const onMenuCommonEvents = (ids: readonly number[]): void => ids.forEach((id) => menuIds.add(id));

  // アクター名（話者推定に使う）
  const actorsJson = asArray(readJson(join(dataDir, 'Actors.json')), 'Actors.json');
  const actors = new Map<number, string>(
    actorsJson.filter(isRecord).map((a) => [asNumber(a['id'], 'Actors.id'), typeof a['name'] === 'string' ? a['name'] : '']),
  );

  // マップ名（MapInfos）
  const mapInfos = asArray(readJson(join(dataDir, 'MapInfos.json')), 'MapInfos.json');
  const mapName = (id: number): string => {
    const info = mapInfos[id];
    return isRecord(info) && typeof info['name'] === 'string' ? info['name'] : '';
  };

  // 1. マップ（ID 順）
  readdirSync(dataDir)
    .filter((f) => /^Map\d{3}\.json$/.test(f))
    .sort()
    .forEach((file) => {
      const id = Number(file.slice(3, 6));
      processMap(file, readJson(join(dataDir, file)), `Map${String(id).padStart(3, '0')} ${mapName(id)}`, actors, collector, onMenuCommonEvents);
    });

  // 2. コモンイベント
  const commonEvents = asArray(readJson(join(dataDir, 'CommonEvents.json')), 'CommonEvents.json');
  commonEvents.forEach((raw) => {
    if (!isRecord(raw)) {
      return;
    }
    const id = asNumber(raw['id'], 'CommonEvents.id');
    const name = typeof raw['name'] === 'string' ? raw['name'] : '';
    processList(
      raw['list'],
      {
        file: 'CommonEvents.json',
        refPrefix: `CE${id}`,
        scene: `CommonEvents.json#CE${id}`,
        sceneTitle: `コモンイベント CE${String(id).padStart(3, '0')} ${name}`,
        resolvePopTarget: (target) => resolveCommonTarget(target, actors),
        onMenuCommonEvents,
      },
      collector,
    );
  });

  // 3. 敵グループのバトルイベント
  asArray(readJson(join(dataDir, 'Troops.json')), 'Troops.json').forEach((raw) => {
    if (!isRecord(raw)) {
      return;
    }
    const id = asNumber(raw['id'], 'Troops.id');
    asArray(raw['pages'], `Troops[${id}].pages`).forEach((pg, p) => {
      processList(
        asRecord(pg, `Troops[${id}].pages[${p}]`)['list'],
        {
          file: 'Troops.json',
          refPrefix: `T${id} p${p}`,
          scene: `Troops.json#${id}`,
          sceneTitle: `敵グループ ${id}`,
          resolvePopTarget: (target) => resolveCommonTarget(target, actors),
          onMenuCommonEvents,
        },
        collector,
      );
    });
  });

  // 4. コモンイベントメニュー（人物・生物データベース）
  processCommonEventMenu(commonEvents, menuIds, collector);

  // 5. データベース
  processDatabase('Actors.json', actorsJson, ['name', 'nickname', 'profile'], 'name', 'name', collector);
  processDatabase('Classes.json', readJson(join(dataDir, 'Classes.json')), ['name'], 'db', 'item', collector);
  processDatabase('Items.json', readJson(join(dataDir, 'Items.json')), ['name', 'description'], 'db', 'item', collector);
  processDatabase('Weapons.json', readJson(join(dataDir, 'Weapons.json')), ['name', 'description'], 'db', 'item', collector);
  processDatabase('Armors.json', readJson(join(dataDir, 'Armors.json')), ['name', 'description'], 'db', 'item', collector);
  processDatabase('Skills.json', readJson(join(dataDir, 'Skills.json')), ['name', 'description', 'message1', 'message2'], 'db', 'item', collector);
  processDatabase('States.json', readJson(join(dataDir, 'States.json')), ['name', 'message1', 'message2', 'message3', 'message4'], 'db', 'item', collector);
  processDatabase('Enemies.json', readJson(join(dataDir, 'Enemies.json')), ['name'], 'db', 'item', collector);

  // 6. システム用語・クエスト・プラグインの文言
  processSystem(readJson(join(dataDir, 'System.json')), collector);
  processQuests(readJson(join(dataDir, 'Quests.json')), collector);
  processPlugins(game.www, collector);

  const units = collector.result();
  const outPath = join('work', 'source', 'units.json');
  writeSourceFile(outPath, { generatedAt: new Date().toISOString(), buildId: game.buildId, units });

  // 原文の制御文字の一覧（原文の文章は含まない）。ゲーム本体が無い環境（CI）でも、
  // build 時に訳文の制御文字が原文と一致しているかを確かめられるようにする。
  const sourceCodes = units
    .map((u) => [unitId(u), listEscapeCodes(u.source).map(normalizeEscapeCode)] as const)
    .filter(([, codes]) => codes.length > 0)
    .sort((a, b) => (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0))
    .reduce<Record<string, string[]>>((acc, [id, codes]) => {
      acc[id] = codes;
      return acc;
    }, {});
  writeJson(SOURCE_CODES_PATH, sourceCodes);

  // 集計を表示する
  const summary = new Map<string, { count: number; words: number }>();
  units.forEach((u) => {
    const s = summary.get(u.ns) ?? { count: 0, words: 0 };
    s.count += 1;
    s.words += u.source.split(/\s+/).filter((w) => w !== '').length;
    summary.set(u.ns, s);
  });
  console.log(`ゲーム: ${game.root}（build ${game.buildId ?? '不明'}）`);
  console.log(`出力: ${outPath}（${units.length} 件）`);
  summary.forEach((s, ns) => console.log(`  ${ns.padEnd(6)} ${String(s.count).padStart(6)} 件  ${String(s.words).padStart(7)} 語`));
  console.log(`コモンイベントメニューの対象: ${menuIds.size} 件`);
};

// 他の CLI と同じく、失敗は例外のメッセージだけを表示して終了コード 1 にする（スタックトレースのまま異常終了させない）
try {
  main();
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
}
