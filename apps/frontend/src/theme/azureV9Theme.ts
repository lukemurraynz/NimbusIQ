/**
 * Azure Portal-aligned Fluent UI v9 theme.
 *
 * Brand palette is derived from the @fluentui/azure-themes AzureThemeLight palette
 * (themePrimary: #0078D4, themeDarker: #004578, themeLighterAlt: #eff6fc) and mapped
 * to Fluent UI v9 BrandVariants (shades 10–160, primary at shade 70).
 */
import { createLightTheme, createDarkTheme, type BrandVariants } from '@fluentui/react-components';
import { AzureThemeLight, AzureThemeDark } from '@fluentui/azure-themes';

// Map @fluentui/azure-themes v8 palette → Fluent UI v9 BrandVariants.
// Shade 70 is the primary brand colour — shifted to themeSecondary (#2b88d8)
// for a lighter, more accessible button appearance while keeping the Azure feel.
const azureBrandVariants: BrandVariants = {
  10: '#f3f9fd',
  20: AzureThemeLight.palette.themeLighterAlt as string, // #eff6fc
  30: AzureThemeLight.palette.themeLighter as string,    // #deecf9
  40: AzureThemeLight.palette.themeLight as string,      // #c7e0f4
  50: AzureThemeLight.palette.themeTertiary as string,   // #71afe5
  60: '#3a96dd',
  70: AzureThemeLight.palette.themeSecondary as string,  // #2b88d8 — primary (lighter)
  80: AzureThemeLight.palette.themePrimary as string,    // #0078D4
  90: AzureThemeLight.palette.themeDarkAlt as string,    // #106ebe
  100: AzureThemeLight.palette.themeDark as string,      // #005a9e
  110: '#003a65',
  120: '#002f52',
  130: '#00243f',
  140: '#00192c',
  150: '#000e19',
  160: '#000408',
};

/** Fluent UI v9 Azure Portal light theme — primary #0078D4 blue. */
export const azureV9LightTheme = createLightTheme(azureBrandVariants);

/**
 * Fluent UI v9 Azure Portal dark theme.
 * Dark mode primary shifts to #106EBE matching AzureThemeDark.palette.themePrimary.
 */
const azureDarkBrandVariants: BrandVariants = {
  10: '#000408',
  20: '#000e19',
  30: '#00192c',
  40: '#00243f',
  50: '#002f52',
  60: '#003a65',
  70: AzureThemeDark.palette.themeDarkAlt as string,  // #106ebe (dark-mode primary)
  80: AzureThemeDark.palette.themeSecondary as string, // #2b88d8
  90: AzureThemeDark.palette.themeTertiary as string,  // #71afe5
  100: AzureThemeDark.palette.themeLight as string,    // #c7e0f4
  110: AzureThemeDark.palette.themeLighter as string,  // #deecf9
  120: AzureThemeDark.palette.themeLighterAlt as string, // #eff6fc
  130: '#f3f9fd',
  140: '#f7fbfe',
  150: '#fafcff',
  160: '#fdfeff',
};

/** Fluent UI v9 Azure Portal dark theme. */
export const azureV9DarkTheme = createDarkTheme(azureDarkBrandVariants);
