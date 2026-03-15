import { makeStyles, tokens, Link } from '@fluentui/react-components';

const useStyles = makeStyles({
  skipLink: {
    position: 'absolute',
    left: '-9999px',
    zIndex: 999,
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorBrandBackground,
    color: tokens.colorNeutralForegroundInverted,
    textDecorationLine: 'none',
    borderRadius: tokens.borderRadiusMedium,
    ':focus': {
      left: tokens.spacingHorizontalM,
      top: tokens.spacingVerticalM,
    },
  },
});

/**
 * Skip to main content link for keyboard navigation accessibility.
 * Appears on focus and allows keyboard users to bypass navigation.
 */
export function SkipToContent() {
  const styles = useStyles();

  const handleSkip = (e: React.MouseEvent<HTMLAnchorElement>) => {
    e.preventDefault();
    const main = document.querySelector('main');
    if (main) {
      main.setAttribute('tabindex', '-1');
      main.focus();
      main.addEventListener('blur', () => main.removeAttribute('tabindex'), { once: true });
    }
  };

  return (
    <Link className={styles.skipLink} href="#main-content" onClick={handleSkip}>
      Skip to main content
    </Link>
  );
}
