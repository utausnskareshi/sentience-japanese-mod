/**
 * Vitest の設定（npm test）。
 *
 * テストの収集範囲を本体の test/ 配下に限定する。vitest の既定（すべてのフォルダの *.test.ts を集める）のままだと、
 * 検証用にリポジトリを複製した work/ 配下や dist/ 配下の *.test.ts まで本体のテストとして実行してしまい、
 * 件数が水増しされたり、無関係な失敗で止まったりするため（tsconfig.json の include とも揃える）。
 *
 * --passWithNoTests は付けない。include を誤ってテストが 0 件になった場合に「成功」とならず、
 * vitest の既定どおり失敗するようにしておく（件数の確認と合わせて、設定の誤りに気付けるようにする）。
 */
import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    include: ['test/**/*.test.ts'],
  },
});
