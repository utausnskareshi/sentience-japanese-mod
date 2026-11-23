/**
 * 用語集・口調設定を作るための資料（翻訳者向け）を作る CLI。
 *
 * 使い方: npm run context
 * 入力:   work/source/units.json
 * 出力:   work/glossary/context.md（原文を含むため Git 管理外）
 *   1. 人物・生物データベースの説明文（キャラクターの背景）
 *   2. 話者ごとの台詞の例（口調を決めるため）
 *   3. 固有名詞の候補（文中の大文字始まりの語の頻度）
 */
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { readSourceFile, type SourceUnit } from './lib/units.ts';
import { parseCliArgs } from './lib/cli.ts';

/** 使い方（引数の誤りのときに表示する） */
const USAGE = 'npm run context（引数はありません）';

/** 話者ごとに載せる台詞の最大数 */
const SAMPLES_PER_SPEAKER = 14;

/** 話者名の表記ゆれ（歩行グラフィック名の接尾辞）をまとめる */
const normalizeSpeaker = (speaker: string): string =>
  speaker
    .replace(/\?$/, '')
    .replace(/(NoCoat|Walking|Running|Follower|_Gun\d*|_Pose\w*|Pose\w*|Injured|NoHand|Nude|_Asterite|B)$/g, '')
    .replace(/^Crew_/, '');

/** 文頭以外に現れる大文字始まりの語を数える（固有名詞の候補） */
const countProperNouns = (units: readonly SourceUnit[]): Array<[string, number]> => {
  const counts = new Map<string, number>();
  const stop = new Set(['I', "I'm", "I'll", "I've", "I'd", 'OK', 'Oh', 'Mr', 'Mrs', 'Ms', 'Dr']);
  units.forEach((u) => {
    u.source.split(/\n/).forEach((line) => {
      const words = line.split(/\s+/);
      words.forEach((word, i) => {
        const clean = word.replace(/^[^A-Za-z]+|[^A-Za-z'-]+$/g, '').replace(/'s$/, '');
        if (clean.length < 2 || !/^[A-Z][a-z]/.test(clean) || stop.has(clean)) {
          return;
        }
        // 文頭（行頭や句点の直後）の語は除外する
        const prev = words[i - 1];
        if (i === 0 || prev === undefined || /[.!?"]$/.test(prev)) {
          return;
        }
        counts.set(clean, (counts.get(clean) ?? 0) + 1);
      });
    });
  });
  return Array.from(counts.entries())
    .filter(([, n]) => n >= 3)
    .sort((a, b) => b[1] - a[1]);
};

const main = (): void => {
  // 引数は受け付けない（打ち間違えたオプションを黙って無視しないよう、あれば使い方を表示して終了する）
  parseCliArgs(process.argv.slice(2), { usage: USAGE, options: {}, allowPositionals: false });
  const source = readSourceFile(join('work', 'source', 'units.json'));
  const lines: string[] = ['# 用語集・口調設定のための資料', ''];

  // 1. 人物・生物データベースの説明文
  lines.push('## 1. 人物・生物データベースの説明文', '');
  source.units
    .filter((u) => u.ns === 'cem' && u.display === 'subtext')
    .forEach((u) => lines.push(`### ${u.sceneTitle}`, u.source, ''));

  // 2. 話者ごとの台詞の例
  const bySpeaker = new Map<string, SourceUnit[]>();
  source.units
    .filter((u) => u.ns === 'msg' && u.speaker !== undefined)
    .forEach((u) => {
      const key = normalizeSpeaker(u.speaker ?? '');
      const list = bySpeaker.get(key) ?? [];
      list.push(u);
      bySpeaker.set(key, list);
    });
  lines.push('## 2. 話者ごとの台詞の例（多い順）', '');
  Array.from(bySpeaker.entries())
    .sort((a, b) => b[1].length - a[1].length)
    .filter(([, list]) => list.length >= 5)
    .forEach(([speaker, list]) => {
      lines.push(`### ${speaker}（${list.length} 件）`);
      // 物語全体から満遍なく拾う
      const step = Math.max(1, Math.floor(list.length / SAMPLES_PER_SPEAKER));
      list
        .filter((_, i) => i % step === 0)
        .slice(0, SAMPLES_PER_SPEAKER)
        .forEach((u) => lines.push(`- ${u.source.replace(/\n/g, ' ')}`));
      lines.push('');
    });

  // 3. 固有名詞の候補
  lines.push('## 3. 固有名詞の候補（文中の大文字始まりの語・出現 3 回以上）', '');
  lines.push(
    countProperNouns(source.units)
      .map(([word, n]) => `${word}(${n})`)
      .join(', '),
    '',
  );

  const outDir = join('work', 'glossary');
  mkdirSync(outDir, { recursive: true });
  const outPath = join(outDir, 'context.md');
  writeFileSync(outPath, `${lines.join('\n')}\n`, 'utf8');
  console.log(`資料を作成しました: ${outPath}（話者 ${bySpeaker.size} 人）`);
};

// 他の CLI と同じく、失敗は 1 行のメッセージで知らせて終了コード 1 にする（スタックトレースのまま異常終了させない）
try {
  main();
} catch (error) {
  console.error(error instanceof Error ? error.message : String(error));
  process.exitCode = 1;
}
