import { AST_NODE_TYPES, type TSESTree } from '@typescript-eslint/utils';
import { createRule } from '../create-rule.ts';

/** String props that are shown to the user and therefore must be translated. */
const TRANSLATED_PROPS = new Set(['title', 'aria-label', 'placeholder', 'alt']);

/** Texts accepted without translation: no letter at all (symbols, digits, punctuation such as "—"). */
const hasLetter = (text: string): boolean => /\p{L}/u.test(text);

type Options = [{ allow?: string[] }];

export const noLiteralStrings = createRule<Options, 'jsxText' | 'jsxProp'>({
  name: 'no-literal-strings',
  meta: {
    type: 'problem',
    docs: {
      description: 'User-visible JSX text and the props title, aria-label, placeholder and alt must come from t().',
    },
    messages: {
      jsxText: 'Literal text "{{text}}" in JSX: use t() from @watchpoint/i18n.',
      jsxProp: 'Literal string in prop "{{prop}}": use t() from @watchpoint/i18n.',
    },
    schema: [
      {
        type: 'object',
        properties: { allow: { type: 'array', items: { type: 'string' } } },
        additionalProperties: false,
      },
    ],
  },
  defaultOptions: [{ allow: [] }],
  create(context, [{ allow = [] }]) {
    const allowed = new Set(allow);
    const isAllowed = (text: string): boolean => {
      const trimmed = text.trim();
      return trimmed === '' || allowed.has(trimmed) || !hasLetter(trimmed);
    };

    const literalText = (node: TSESTree.Node | null): string | undefined => {
      if (node?.type === AST_NODE_TYPES.Literal && typeof node.value === 'string') {
        return node.value;
      }
      if (node?.type === AST_NODE_TYPES.TemplateLiteral && node.expressions.length === 0) {
        return node.quasis.map((q) => q.value.cooked ?? '').join('');
      }
      if (
        node?.type === AST_NODE_TYPES.JSXExpressionContainer &&
        node.expression.type !== AST_NODE_TYPES.JSXEmptyExpression
      ) {
        return literalText(node.expression);
      }
      return undefined;
    };

    return {
      JSXText(node) {
        if (!isAllowed(node.value)) {
          context.report({ node, messageId: 'jsxText', data: { text: node.value.trim() } });
        }
      },
      JSXExpressionContainer(node) {
        // {"text"} or {`text`} as a child of an element.
        if (node.parent.type !== AST_NODE_TYPES.JSXAttribute) {
          const text = literalText(node);
          if (text !== undefined && !isAllowed(text)) {
            context.report({ node, messageId: 'jsxText', data: { text: text.trim() } });
          }
        }
      },
      JSXAttribute(node) {
        const prop =
          node.name.type === AST_NODE_TYPES.JSXIdentifier
            ? node.name.name
            : `${node.name.namespace.name}:${node.name.name.name}`;
        if (!TRANSLATED_PROPS.has(prop)) {
          return;
        }
        const text = literalText(node.value);
        if (text !== undefined && !isAllowed(text)) {
          context.report({ node, messageId: 'jsxProp', data: { prop } });
        }
      },
    };
  },
});
