(() => {
    const oidcUrl = new URL('.', document.currentScript.src);
    const serverUrl = new URL('../', oidcUrl);
    const endpoint = () => oidcUrl + 'start?returnUrl=' + encodeURIComponent(location.hash.startsWith('#!') ? location.hash.slice(2) : '/');
    const isLoginPage = () => location.hash.includes('login');
    const hasOidcError = () => location.search.includes('oidcError=1') || location.hash.includes('oidcError=1');
    const isSignedOut = () => location.search.includes('oidcSignedOut=1');
    const loginLabel = () => window.oidcButtonText || 'Sign In with SSO';
    const signedOutLabel = () => window.oidcSignedOutText || 'Signed Out';
    const revealLogin = () => document.querySelector('#oidc-login-redirect-style')?.remove();
    const isPrimaryLogin = () => window.oidcPasswordLoginMode === 'DisableForAllUsers';
    let loginObserver;
    let shownError;
    let redirectingToProvider;
    const loadStrings = async () => {
        const locale = document.documentElement.lang.toLowerCase();
        for (const value of new Set([locale, locale.split('-')[0], 'en-us'])) {
            const response = await fetch(oidcUrl + 'strings/' + encodeURIComponent(value));
            if (response.ok) return response.json();
        }
        return {};
    };
    const updateLoginContainer = () => {
        const login = document.querySelector('#loginPage');
        const hidesLocalLogin = window.oidcPasswordLoginMode === 'DisableForAllUsers' && !location.search.includes('oidcTicket=');
        document.querySelectorAll('#loginPage .manualLoginForm:not([data-oidc-login-container]), #loginPage #divUsers, #loginPage .btnManual, #loginPage .btnForgotPassword').forEach(control => {
            if (hidesLocalLogin) control.style.setProperty('display', 'none', 'important');
            else control.style.removeProperty('display');
        });
        const button = document.querySelector('[data-oidc-login]');
        if (!button) return;
        const container = button.closest('[data-oidc-login-container]');
        const status = login?.querySelector('.visualLoginForm');
        if (isPrimaryLogin() && !container) {
            const form = document.createElement('div');
            form.className = 'manualLoginForm'; form.dataset.oidcLoginContainer = 'true';
            button.replaceWith(form); if (status) form.append(status); form.append(button);
        } else if (isPrimaryLogin() && container && status && !container.contains(status)) {
            container.prepend(status);
        } else if (!isPrimaryLogin() && container) {
            const containerStatus = container.querySelector('.visualLoginForm');
            if (containerStatus) container.before(containerStatus);
            container.replaceWith(button);
        }
    };
    const addLogin = stack => {
        if (!isLoginPage()) {
            document.querySelectorAll('[data-oidc-login-container]').forEach(container => {
                const status = container.querySelector('.visualLoginForm');
                if (status) container.before(status);
                container.remove();
            });
            document.querySelectorAll('[data-oidc-login]').forEach(button => button.remove());
            return;
        }
        if (!stack) {
            document.querySelectorAll('.readOnlyContent').forEach(addLogin);
            return;
        }
        if (stack.querySelector('[data-oidc-login]')) {
            updateLoginContainer();
            return;
        }
        const button = document.createElement('button');
        button.type = 'button'; button.setAttribute('is', 'emby-button'); button.className = 'raised block' + (isPrimaryLogin() ? ' button-submit' : '');
        button.dataset.oidcLogin = 'true'; button.setAttribute('aria-label', loginLabel());
        const label = document.createElement('span'); label.textContent = loginLabel();
        button.append(label);
        button.onclick = () => {
            button.disabled = true;
            button.setAttribute('aria-label', 'Redirecting...');
            label.textContent = 'Redirecting...';
            location.assign(endpoint());
        };
        stack.insertBefore(button, stack.querySelector('.btnQuick'));
        updateLoginContainer();
    };
    const resetLoginButton = () => {
        const button = document.querySelector('[data-oidc-login]');
        if (!button) return;
        button.disabled = false;
        button.setAttribute('aria-label', loginLabel());
        button.querySelector('span').textContent = loginLabel();
        button.classList.toggle('button-submit', isPrimaryLogin());
        updateLoginContainer();
    };
    const showSignedOut = () => {
        if (!isLoginPage() || !isSignedOut()) return false;
        let shown = false;
        for (const login of document.querySelectorAll('#loginPage')) {
            const status = login.querySelector('.visualLoginForm');
            const stack = login.querySelector('.readOnlyContent');
            const heading = status?.querySelector('h1');
            if (!heading || !stack) continue;
            if (heading.textContent !== signedOutLabel()) heading.textContent = signedOutLabel();
            addLogin(stack);
            stack.querySelector('[data-oidc-login]')?.classList.add('button-submit');
            login.style.visibility = 'visible';
            shown = true;
        }
        return shown;
    };
    const showError = () => {
        if (!hasOidcError()) {
            shownError = false;
            return;
        }
        if (!isLoginPage() || document.querySelector('[data-oidc-error]') || shownError) return;
        shownError = true;
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
        const logout = event.target.closest('.btnLogout') || (menuItem?.querySelector('[data-testid="LogoutIcon"]') ? menuItem : null);
        if (!logout || (!window.oidcRpInitiatedLogout && !window.oidcRedirectSignInPageToProvider)) return;
        event.preventDefault(); event.stopImmediatePropagation();
        const credentials = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}');
        const server = credentials.Servers?.[0];
        if (server?.AccessToken) await fetch(serverUrl + 'Sessions/Logout', { method: 'POST', headers: { Authorization: `MediaBrowser Token="${server.AccessToken}"` } });
        for (const savedServer of credentials.Servers || []) {
            savedServer.UserId = null; savedServer.AccessToken = null; savedServer.ExchangeToken = null;
        }
        localStorage.setItem('jellyfin_credentials', JSON.stringify(credentials));
        location.assign(oidcUrl + 'logout');
    }, true);
    const redirectToProvider = async () => {
        if (redirectingToProvider || !isLoginPage() || isSignedOut() || !window.oidcRedirectSignInPageToProvider) return;
        if (hasOidcError()) {
            revealLogin();
            return;
        }
        const server = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}').Servers?.[0];
        if (server?.AccessToken && await fetch(serverUrl + 'Users/Me', { headers: { Authorization: `MediaBrowser Token="${server.AccessToken}"` } }).then(response => response.ok).catch(() => false)) return;
        if (redirectingToProvider) return;
        redirectingToProvider = true;
        location.assign(endpoint());
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
        showError();
    };
    addEventListener('oidcconfigurationchange', () => { void configure(); });
    addEventListener('hashchange', () => {
        if (isSignedOut() && isLoginPage()) {
            location.reload();
            return;
        }
        addLogin();
        showSignedOut();
        showError();
        void redirectToProvider();
    });
    addEventListener('pageshow', () => { resetLoginButton(); void redirectToProvider(); });
    enableLogout();
    loginObserver = new MutationObserver(() => { addLogin(); showSignedOut(); showError(); void redirectToProvider(); });
    loginObserver.observe(document.documentElement, { childList: true, subtree: true });
    addLogin();
    updateLoginContainer();
    void redirectToProvider();
    void configure();
})();
