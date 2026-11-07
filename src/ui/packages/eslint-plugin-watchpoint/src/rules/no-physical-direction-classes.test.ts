import { ruleTester } from '../rule-tester.ts';
import { noPhysicalDirectionClasses } from './no-physical-direction-classes.ts';

ruleTester.run('no-physical-direction-classes', noPhysicalDirectionClasses, {
  valid: [
    {
      code: 'const a = <div className="ms-2 pe-4 start-0 text-start rounded-s border-e mt-2 px-3" />;',
      filename: 'a.tsx',
    },
    { code: 'const a = cn("ms-2", cond && "me-1", { "ps-2": on });', filename: 'a.tsx' },
    { code: 'const a = "ml-2";', filename: 'a.tsx' },
    { code: 'const a = <div className="leftover-class margin-left" />;', filename: 'a.tsx' },
  ],
  invalid: [
    {
      code: 'const a = <div className="ml-2" />;',
      filename: 'a.tsx',
      errors: [{ messageId: 'physical', data: { className: 'ml-2', replacement: 'ms-2' } }],
    },
    {
      code: 'const a = <div className="md:pr-4 hover:-mr-1 text-right left-0 rounded-tl-lg border-l-2" />;',
      filename: 'a.tsx',
      errors: [
        { messageId: 'physical', data: { className: 'md:pr-4', replacement: 'pe-4' } },
        { messageId: 'physical', data: { className: 'hover:-mr-1', replacement: '-me-1' } },
        { messageId: 'physical', data: { className: 'text-right', replacement: 'text-end' } },
        { messageId: 'physical', data: { className: 'left-0', replacement: 'start-0' } },
        { messageId: 'physical', data: { className: 'rounded-tl-lg', replacement: 'rounded-ss-lg' } },
        { messageId: 'physical', data: { className: 'border-l-2', replacement: 'border-s-2' } },
      ],
    },
    {
      code: 'const a = <div className={`flex ${on ? "pl-2" : "ps-2"}`} />;',
      filename: 'a.tsx',
      errors: [{ messageId: 'physical' }],
    },
    {
      code: 'const a = clsx("rounded-l", cond && "float-left", { "mr-2": on });',
      filename: 'a.tsx',
      errors: [{ messageId: 'physical' }, { messageId: 'physical' }, { messageId: 'physical' }],
    },
  ],
});
