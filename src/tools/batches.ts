/**
 * 翻訳作業用のバッチ（作業単位）を作る CLI。
 *
 * 使い方: npm run batches -- [--words 3500] [--force]
 * 既存のバッチ（work/batches/index.json）がある場合は、--force を付けたときだけ作り直す。
 * 入力:   work/source/units.json（npm run extract の出力）
 * 出力:   work/batches/<ID>.md   … 場面ごとに並べた原文（翻訳者が読む台本）
 *         work/batches/<ID>.json … バッチに含まれる識別子の一覧
 *         work/batches/index.json … バッチの一覧
 *
 * 既に translation/ja に訳文があるユニットは除外する（途中から再開できる）。
 * work/ は原文を含むため Git 管理外。
 */
import { existsSync, mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { parseCliArgs } from './lib/cli.ts';
import { writeJson } from './lib/json.ts';
import { readSourceFile, unitId, type SourceUnit } from './lib/units.ts';
import { readTables } from './lib/translations.ts';

/** バッチの出力先 */
const BATCH_DIR = join('work', 'batches');

/** 使い方（引数の誤りのときに表示する） */
const USAGE = 'npm run batches -- [--words <語数>] [--force]';

/** 表示種別の日本語ラベル */
const DISPLAY_LABEL: Readonly<Record<SourceUnit['display'], string>> = {
  pop: '吹き出し',
  window: 'ウィンドウ',
  scroll: 'スクロール文章',
  choice: '選択肢',
  name: 'アクター名',
  item: 'データベース',
  term: '用語・UI',
  map: 'マップ名',
  quest: 'ミッションログ',
  menu: 'メニュー項目名',
  subtext: '人物・生物の説明文',
};

/** 1 バッチ */
interface Batch {
  readonly id: string;
  readonly units: SourceUnit[];
  words: number;
}

/** 英単語数（バッチの大きさの目安） */
const countWords = (text: string): number => text.split(/\s+/).filter((w) => w !== '').length;

/** 場面ごとにまとめる（初出順を保つ） */
const groupByScene = (units: readonly SourceUnit[]): SourceUnit[][] => {
  const scenes = new Map<string, SourceUnit[]>();
  units.forEach((u) => {
    const list = scenes.get(u.scene) ?? [];
    list.push(u);
    scenes.set(u.scene, list);
  });
  return Array.from(scenes.values());
};

/** 場面の列を、語数の上限に収まるようにバッチへ詰める（大きな場面は分割する） */
const packScenes = (prefix: string, scenes: readonly SourceUnit[][], maxWords: number): Batch[] => {
  const batches: Batch[] = [];
  const newBatch = (): Batch => {
    const batch: Batch = { id: `${prefix}-${String(batches.length + 1).padStart(3, '0')}`, units: [], words: 0 };
    batches.push(batch);
    return batch;
  };
  let current = newBatch();
  scenes.forEach((scene) => {
    const sceneWords = scene.reduce((acc, u) => acc + countWords(u.source), 0);
    if (current.words > 0 && current.words + sceneWords > maxWords) {
      current = newBatch();
    }
    scene.forEach((unit) => {
      const words = countWords(unit.source);
      // 1 つの場面が上限を大きく超える場合は場面の途中で分割する
      if (current.words > 0 && current.words + words > maxWords * 1.3) {
        current = newBatch();
      }
      current.units.push(unit);
      current.words += words;
    });
  });
  return batches.filter((b) => b.units.length > 0);
};

/** バッチの台本（Markdown）を作る */
const renderBatch = (batch: Batch): string => {
  const lines: string[] = [`# バッチ ${batch.id}（${batch.units.length} 件・約 ${batch.words} 語）`, ''];
  let lastScene = '';
  batch.units.forEach((u) => {
    if (u.scene !== lastScene) {
      lines.push(`## ${u.sceneTitle}`, '');
      lastScene = u.scene;
    }
    const meta = [DISPLAY_LABEL[u.display], u.speaker === undefined ? undefined : `話者: ${u.speaker}`, u.count > 1 ? `${u.count}回使用` : undefined]
      .filter((v): v is string => v !== undefined)
      .join('｜');
    lines.push(`### [${unitId(u)}] ${meta}`, u.source, '');
  });
  return `${lines.join('\n')}\n`;
};

const main = (): void => {
  // 未知のオプション・値の無い --words は使い方を表示して終了する（lib/cli.ts）
  const { values } = parseCliArgs(process.argv.slice(2), { usage: USAGE, options: { words: 'string', force: 'boolean' }, allowPositionals: false });
  const maxWords = Number(values.words ?? '3500');
  if (!Number.isInteger(maxWords) || maxWords <= 0) {
    throw new Error(`--words には正の整数を指定してください（指定値: ${values.words ?? ''}）`);
  }
  // 既存のバッチを作り直すと ID が振り直され、作業中のバッチや --batch の検査と食い違うため、
  // 明示的に --force を付けたときだけ作り直す
  if (existsSync(join(BATCH_DIR, 'index.json')) && !values.force) {
    throw new Error(`${BATCH_DIR} に既存のバッチがあります。作業中のバッチが無いことを確認し、--force を付けて実行してください`);
  }
  const source = readSourceFile(join('work', 'source', 'units.json'));
  const tables = readTables();
  const pending = source.units.filter((u) => tables[u.ns][u.key] === undefined);

  // 固有名詞を先に決めるため、会話以外（データベース等）を別系統のバッチにする
  const isDialogue = (u: SourceUnit): boolean => u.ns === 'msg' || u.ns === 'choice';
  const misc = packScenes('misc', groupByScene(pending.filter((u) => !isDialogue(u))), maxWords);
  const dialogue = packScenes('dlg', groupByScene(pending.filter(isDialogue)), maxWords);
  const batches = [...misc, ...dialogue];

  rmSync(BATCH_DIR, { recursive: true, force: true });
  mkdirSync(BATCH_DIR, { recursive: true });
  batches.forEach((batch) => {
    writeFileSync(join(BATCH_DIR, `${batch.id}.md`), renderBatch(batch), 'utf8');
    writeJson(join(BATCH_DIR, `${batch.id}.json`), batch.units.map(unitId));
  });
  writeJson(
    join(BATCH_DIR, 'index.json'),
    batches.map((b) => ({ id: b.id, units: b.units.length, words: b.words, firstScene: b.units[0]?.sceneTitle ?? '' })),
  );
  console.log(`未翻訳 ${pending.length} 件 → ${batches.length} バッチ（上限 ${maxWords} 語）を ${BATCH_DIR} に作成しました`);
  batches.forEach((b) => console.log(`  ${b.id}  ${String(b.units.length).padStart(5)} 件  ${String(b.words).padStart(6)} 語  ${b.units[0]?.sceneTitle ?? ''}`));
};

try {
  main();
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
}
