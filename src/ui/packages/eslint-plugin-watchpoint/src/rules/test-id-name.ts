import { AST_NODE_TYPES, type TSESTree } from '@typescript-eslint/utils';
import { createRule } from '../create-rule.ts';

/** A test id such as UIC-U-012 or X-API-REG-C-001 (Architecture/README.md §9.2), followed by a space or the end. */
export const TEST_ID_PATTERN = /^[A-Z][A-Z0-9]*(-[A-Z][A-Z0-9]*)*-[UICEPS]-\d{3}(\s|$)/;

const TEST_FUNCTIONS = new Set(['it', 'test']);

/** Returns the test function name of `it(…)`, `test.only(…)`, `it.each(…)(…)`, or undefined. */
function testFunctionName(callee: TSESTree.Expression): string | undefined {
  let node: TSESTree.Node = callee;
  // it.each([...])('name', fn): the callee is itself a call.
  if (node.type === AST_NODE_TYPES.CallExpression) {
    node = node.callee;
  }
  while (node.type === AST_NODE_TYPES.MemberExpression) {
    node = node.object;
  }
  return node.type === AST_NODE_TYPES.Identifier && TEST_FUNCTIONS.has(node.name) ? node.name : undefined;
}

export const testIdName = createRule<[], 'missingId'>({
  name: 'test-id-name',
  meta: {
    type: 'problem',
    docs: { description: 'Test names must start with a test id, e.g. "UIC-U-012 …".' },
    messages: { missingId: 'Test name "{{name}}" must start with a test id such as "UIC-U-012 ".' },
    schema: [],
  },
  defaultOptions: [],
  create(context) {
    return {
      CallExpression(node) {
        if (testFunctionName(node.callee) === undefined) {
          return;
        }
        // it.each(table) itself is not a test declaration; the call it returns is.
        if (node.parent.type === AST_NODE_TYPES.CallExpression && node.parent.callee === node) {
          return;
        }
        const first = node.arguments[0];
        let name: string | undefined;
        if (first?.type === AST_NODE_TYPES.Literal && typeof first.value === 'string') {
          name = first.value;
        } else if (first?.type === AST_NODE_TYPES.TemplateLiteral) {
          name = first.quasis[0]?.value.cooked ?? '';
        }
        if (name !== undefined && !TEST_ID_PATTERN.test(name)) {
          context.report({ node: first ?? node, messageId: 'missingId', data: { name } });
        }
      },
    };
  },
});
