import type { ThemeConfig } from 'antd';

/**
 * antd's defaults are Ant blue with 6 px radii; this app's approved design pack
 * is navy chrome, a teal action colour and half-rem controls. Mapping the tokens
 * here means a component taken from antd looks like it was drawn for this app
 * rather than dropped in from another product.
 */
export const antdTheme: ThemeConfig = {
  token: {
    colorPrimary: '#0f766e',
    colorError: '#ba1a1a',
    colorSuccess: '#047857',
    colorWarning: '#b45309',
    colorInfo: '#1d4ed8',
    colorText: '#0b1c30',
    colorTextSecondary: '#475569',
    colorBorder: '#e2e8f0',
    colorBorderSecondary: '#eef2f7',
    colorBgContainer: '#ffffff',
    colorBgLayout: '#f8f9ff',
    borderRadius: 8,
    fontSize: 14,
    fontFamily: "'Inter', ui-sans-serif, system-ui, sans-serif",
    controlHeight: 36,
  },
  components: {
    Modal: {
      borderRadiusLG: 12,
      paddingContentHorizontalLG: 20,
    },
    Select: {
      optionSelectedBg: '#eff4ff',
    },
  },
};
