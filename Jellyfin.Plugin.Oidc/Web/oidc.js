(() => {
    const oidcUrl = new URL('.', document.currentScript.src);
    const serverUrl = new URL('../', oidcUrl);
    const endpoint = oidcUrl + 'start?returnUrl=' + encodeURIComponent(location.hash.startsWith('#!') ? location.hash.slice(2) : '/');
    const isLoginPage = () => location.hash.includes('login');
    const hasOidcError = () => location.search.includes('oidcError=1') || location.hash.includes('oidcError=1');
    const isSignedOut = () => location.search.includes('oidcSignedOut=1');
    const loginLabel = () => window.oidcButtonText || 'Sign In with SSO';
    const signedOutLabel = () => window.oidcSignedOutText || 'Signed Out';
    let loginObserver;
    const loadStrings = async () => {
        const locale = document.documentElement.lang.toLowerCase();
        for (const value of new Set([locale, locale.split('-')[0], 'en-us'])) {
            const response = await fetch(oidcUrl + 'strings/' + encodeURIComponent(value));
            if (response.ok) return response.json();
        }
        return {};
    };
    const addLogin = (stack = document.querySelector('.readOnlyContent')) => {
        if (!isLoginPage()) {
            document.querySelector('[data-oidc-login]')?.remove();
            return;
        }
        if (document.querySelector('[data-oidc-login]')) return;
        if (!stack) return;
        const button = document.createElement('button');
        button.type = 'button'; button.setAttribute('is', 'emby-button'); button.className = 'raised cancel block';
        button.dataset.oidcLogin = 'true'; button.setAttribute('aria-label', loginLabel());
        const label = document.createElement('span'); label.textContent = loginLabel();
        button.append(label);
        button.onclick = () => {
            button.disabled = true;
            button.setAttribute('aria-label', 'Redirecting...');
            label.textContent = 'Redirecting...';
            requestAnimationFrame(() => location.assign(endpoint));
        };
        stack.insertBefore(button, stack.querySelector('.btnQuick'));
    };
    const resetLoginButton = () => {
        const button = document.querySelector('[data-oidc-login]');
        if (!button) return;
        button.disabled = false;
        button.setAttribute('aria-label', loginLabel());
        button.querySelector('span').textContent = loginLabel();
    };
    const showSignedOut = () => {
        if (!isLoginPage() || !isSignedOut()) return false;
        const login = document.querySelector('#loginPage');
        const content = login?.firstElementChild;
        const status = content?.querySelector('.visualLoginForm');
        const stack = content?.querySelector('.readOnlyContent');
        if (!content || !status || !stack) return false;
        content.replaceChildren(status, stack);
        status.replaceChildren();
        const heading = document.createElement('h1');
        heading.className = 'sectionTitle'; heading.style.marginTop = '1em'; heading.textContent = signedOutLabel();
        status.append(heading);
        stack.replaceChildren();
        addLogin(stack);
        login.style.visibility = 'visible';
        return true;
    };
    const removeLocalLogin = () => {
        if (!isLoginPage()) return;
        const login = document.querySelector('#loginPage');
        const visual = login?.querySelector('.visualLoginForm');
        const stack = login?.querySelector('.readOnlyContent');
        const isOidcOnly = stack?.children.length === 1 && stack.querySelector('[data-oidc-login]') && !login?.querySelector('.manualLoginForm, #divUsers');
        if (!visual || !stack || isOidcOnly) return;
        login.querySelector('.manualLoginForm')?.remove();
        visual.querySelector('#divUsers')?.remove();
        stack.replaceChildren();
    };
    const showError = () => {
        if (!isLoginPage() || !hasOidcError() || document.querySelector('[data-oidc-error]')) return;
        const container = document.querySelector('.toastContainer') || document.body.appendChild(document.createElement('div'));
        container.classList.add('toastContainer');
        const message = document.createElement('div');
        message.dataset.oidcError = 'true'; message.setAttribute('role', 'alert');
        message.className = 'toast'; message.textContent = 'We couldn\'t sign you in. Please try again.';
        container.append(message);
        setTimeout(() => message.classList.add('toastVisible'), 300);
        setTimeout(() => message.remove(), 3600);
    };
    const enableLogout = () => addEventListener('click', async event => {
        const menuItem = event.target.closest('[role="menuitem"]');
        const logout = menuItem?.querySelector('[data-testid="LogoutIcon"]') ? menuItem : null;
        if (!logout || (!window.oidcRpInitiatedLogout && !window.oidcRedirectSignInPageToProvider)) return;
        event.preventDefault(); event.stopImmediatePropagation();
        const server = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}').Servers?.[0];
        if (server?.AccessToken) await fetch(serverUrl + 'Sessions/Logout', { method: 'POST', headers: { Authorization: `MediaBrowser Token="${server.AccessToken}"` } });
        localStorage.clear(); sessionStorage.removeItem('oidcStarted'); location.assign(oidcUrl + 'logout');
    }, true);
    const redirectToProvider = async () => {
        if (!isLoginPage() || isSignedOut() || !window.oidcRedirectSignInPageToProvider || hasOidcError() || sessionStorage.oidcStarted) return;
        const server = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}').Servers?.[0];
        if (server?.AccessToken && await fetch(serverUrl + 'Users/Me', { headers: { Authorization: `MediaBrowser Token="${server.AccessToken}"` } }).then(response => response.ok).catch(() => false)) return;
        sessionStorage.oidcStarted = 'true';
        location.assign(endpoint);
    };
    const configure = async () => {
        const response = await fetch(oidcUrl + 'config', { cache: 'no-store' });
        const config = response.ok ? await response.json() : null;
        if (!config) return;
        window.oidcButtonText = config.LoginButtonText;
        window.oidcRpInitiatedLogout = config.RpInitiatedLogout;
        window.oidcPasswordLoginMode = config.PasswordLoginMode;
        window.oidcRedirectSignInPageToProvider = config.RedirectSignInPageToProvider;
        resetLoginButton();
        const allPasswordsDisabled = config.PasswordLoginMode === 'DisableForAllUsers';
        const redirectsToProvider = allPasswordsDisabled && config.RedirectSignInPageToProvider;
        const removesLocalLogin = allPasswordsDisabled && !redirectsToProvider;
        if (redirectsToProvider && isSignedOut()) {
            window.oidcSignedOutText = (await loadStrings().catch(() => ({}))).signedOut;
            if (showSignedOut()) return;
            const observer = new MutationObserver(() => {
                if (showSignedOut()) observer.disconnect();
            });
            observer.observe(document.documentElement, { childList: true, subtree: true });
            return;
        }
        if (redirectsToProvider) void redirectToProvider();
        loginObserver?.disconnect();
        loginObserver = new MutationObserver(() => { if (removesLocalLogin) removeLocalLogin(); addLogin(); });
        loginObserver.observe(document.documentElement, { childList: true, subtree: true });
        if (removesLocalLogin) removeLocalLogin();
        addLogin();
        showError();
    };
    addEventListener('oidcconfigurationchange', () => { void configure(); });
    addEventListener('hashchange', () => { addLogin(); showError(); void redirectToProvider(); });
    addEventListener('pageshow', resetLoginButton);
    enableLogout();
    void configure();
})();
