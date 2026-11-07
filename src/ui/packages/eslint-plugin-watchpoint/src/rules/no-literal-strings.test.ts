import { ruleTester } from '../rule-tester.ts';
import { noLiteralStrings } from './no-literal-strings.ts';

ruleTester.run('no-literal-strings', noLiteralStrings, {
  valid: [
    { code: 'const a = <p>{t("devices.title")}</p>;', filename: 'a.tsx' },
    { code: 'const a = <p> — </p>;', filename: 'a.tsx' },
    { code: 'const a = <p>{count} / 100</p>;', filename: 'a.tsx' },
    { code: 'const a = <button title={t("save")} aria-label={t("save")} />;', filename: 'a.tsx' },
    { code: 'const a = <div className="flex gap-2" data-testid="row" />;', filename: 'a.tsx' },
    { code: 'const a = <p>OK</p>;', filename: 'a.tsx', options: [{ allow: ['OK'] }] },
  ],
  invalid: [
    { code: 'const a = <p>Save</p>;', filename: 'a.tsx', errors: [{ messageId: 'jsxText' }] },
    { code: 'const a = <p>{"Save"}</p>;', filename: 'a.tsx', errors: [{ messageId: 'jsxText' }] },
    { code: 'const a = <p>{`Save`}</p>;', filename: 'a.tsx', errors: [{ messageId: 'jsxText' }] },
    { code: 'const a = <img alt="Logo" />;', filename: 'a.tsx', errors: [{ messageId: 'jsxProp' }] },
    { code: 'const a = <input placeholder={"Search"} />;', filename: 'a.tsx', errors: [{ messageId: 'jsxProp' }] },
    {
      code: 'const a = <button title="Save" aria-label="Save" />;',
      filename: 'a.tsx',
      errors: [{ messageId: 'jsxProp' }, { messageId: 'jsxProp' }],
    },
    { code: 'const a = <p>مرحبا</p>;', filename: 'a.tsx', errors: [{ messageId: 'jsxText' }] },
  ],
});
