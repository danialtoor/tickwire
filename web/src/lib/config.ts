// Set at build time once the public repo exists (VITE_REPO_URL); falls back to GitHub's home page.
export const REPO_URL: string = import.meta.env.VITE_REPO_URL ?? 'https://github.com'
