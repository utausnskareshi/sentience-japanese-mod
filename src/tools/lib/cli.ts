/**
 * 作業用 CLI の引数の解析（純粋関数）。すべての CLI（extract・context・batches・merge・verify・build・package・dev）で使う。
 *
 * 既知のオプションの一覧で引数を検証し、次の場合は「使い方」を含む例外を投げる。
 * 呼び出し側の main の try/catch がメッセージを表示し、終了コード 1 で終わる。
 * - 未知のオプション（--dryrun・--dry_run のような打ち間違いを、黙って無視して本番の処理を行わないため）
 * - 値を取るオプションの値が無い・空（--batch だけで ID を書き忘れると、全ファイルを統合してしまうため）
 * - 値を取らないオプションに値がある（--force=yes など）、値を取るオプションの重複
 * - オプション以外の引数を受け付けない CLI に引数がある
 *
 * 書式: --name（値なし）、--name value、--name=value。"--" より後ろはすべてオプション以外の引数として扱う。
 * 値が "-" で始まる場合は、オプションの書き忘れと区別できないため --name=value の形でだけ受け付ける
 * （node:util の parseArgs の strict と同じ考え方）。
 */

/** オプションの種類（boolean: 値なしのフラグ、string: 値を 1 つ取る） */
export type OptionType = 'boolean' | 'string';

/** CLI の引数の定義 */
export interface CliSpec<O extends Readonly<Record<string, OptionType>>> {
  /** 使い方（引数の誤りのときに表示する） */
  readonly usage: string;
  /** 受け付けるオプション（先頭の "--" を除いた名前 → 種類） */
  readonly options: O;
  /** オプション以外の引数（ファイル名など）を受け付けるか */
  readonly allowPositionals: boolean;
}

/** オプションの値（boolean は指定の有無、string は値。指定が無ければ undefined） */
export type CliValues<O extends Readonly<Record<string, OptionType>>> = {
  readonly [K in keyof O]: O[K] extends 'boolean' ? boolean : string | undefined;
};

/** 解析結果 */
export interface CliArgs<O extends Readonly<Record<string, OptionType>>> {
  readonly values: CliValues<O>;
  /** オプション以外の引数（指定順） */
  readonly positionals: readonly string[];
}

/**
 * コマンドライン引数を解析する（純粋関数）。誤りがあれば、理由と使い方を含む例外を投げる。
 *
 * @param args process.argv.slice(2)
 * @param spec 受け付けるオプションと使い方
 */
export const parseCliArgs = <O extends Readonly<Record<string, OptionType>>>(args: readonly string[], spec: CliSpec<O>): CliArgs<O> => {
  /** 理由と使い方を含む例外を投げる */
  const fail = (reason: string): never => {
    throw new Error(`${reason}\n使い方: ${spec.usage}`);
  };
  /** 定義済みのオプションの種類（Object.prototype のプロパティ名を拾わないよう、自分のプロパティだけを見る） */
  const typeOf = (key: string): OptionType | undefined => (Object.hasOwn(spec.options, key) ? spec.options[key] : undefined);

  const values = new Map<string, string | true>();
  const positionals: string[] = [];
  for (let i = 0; i < args.length; i++) {
    const arg = args[i] ?? '';
    if (arg === '--') {
      positionals.push(...args.slice(i + 1));
      break;
    }
    if (!arg.startsWith('-') || arg === '-') {
      positionals.push(arg);
      continue;
    }
    const eq = arg.indexOf('=');
    const name = eq >= 0 ? arg.slice(0, eq) : arg;
    const inline = eq >= 0 ? arg.slice(eq + 1) : undefined;
    const type = name.startsWith('--') ? typeOf(name.slice(2)) : undefined;
    if (type === undefined) {
      return fail(`不明なオプションです: ${name}`);
    }
    const key = name.slice(2);
    if (type === 'boolean') {
      if (inline !== undefined) {
        return fail(`${name} は値を取りません（指定値: ${inline}）`);
      }
      values.set(key, true);
      continue;
    }
    // 値を取るオプション: --name=value か、次の引数（"-" で始まるものはオプションの書き忘れとみなす）
    const next = args[i + 1];
    const value = inline ?? (next !== undefined && !(next.startsWith('-') && next !== '-') ? next : undefined);
    if (value === undefined) {
      return fail(`${name} の値がありません`);
    }
    if (value === '') {
      return fail(`${name} の値が空です`);
    }
    if (values.has(key)) {
      return fail(`${name} が複数回指定されています`);
    }
    values.set(key, value);
    if (inline === undefined) {
      i += 1;
    }
  }
  if (!spec.allowPositionals && positionals.length > 0) {
    return fail(`不要な引数があります: ${positionals.join(' ')}`);
  }
  const result = Object.fromEntries(
    Object.keys(spec.options).map((key) => {
      const value = values.get(key);
      return [key, spec.options[key] === 'boolean' ? value === true : typeof value === 'string' ? value : undefined];
    }),
  );
  // Object.fromEntries はキーごとの型を保てないため、上で定義どおりに作った値を型だけ付け直す
  return { values: result as CliValues<O>, positionals };
};
