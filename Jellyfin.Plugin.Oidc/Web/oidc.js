(() => {
    const endpoint = '/oidc/start?returnUrl=' + encodeURIComponent(location.hash.startsWith('#!') ? location.hash.slice(2) : '/');
    const isLoginPage = () => location.hash.includes('login');
    const hasOidcError = () => location.hash.includes('oidcError=1');
    const addLogin = () => {
        if (!isLoginPage()) {
            document.querySelector('[data-oidc-login]')?.remove();
            return;
        }
        if (document.querySelector('[data-oidc-login]')) return;
        const stack = document.querySelector('.readOnlyContent');
        if (!stack) return;
        const button = document.createElement('button');
        button.type = 'button'; button.setAttribute('is', 'emby-button'); button.className = 'raised cancel block';
        button.style.cssText = 'align-items:center;display:flex;gap:.5em;justify-content:center';
        button.dataset.oidcLogin = 'true'; button.setAttribute('aria-label', window.oidcButtonText || 'Login with SSO');
        const icon = document.createElement('span');
        icon.className = 'material-icons'; icon.textContent = 'vpn_key'; icon.setAttribute('aria-hidden', 'true');
        const label = document.createElement('span'); label.textContent = window.oidcButtonText || 'Login with SSO';
        button.append(icon, label);
        button.onclick = () => {
            button.disabled = true;
            button.setAttribute('aria-label', 'Redirecting...');
            label.textContent = 'Redirecting...';
            requestAnimationFrame(() => location.assign(endpoint));
        };
        stack.insertBefore(button, stack.querySelector('.btnQuick'));
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
        const logout = event.target.closest('.btnLogout');
        if (!logout || !window.oidcRpInitiatedLogout) return;
        event.preventDefault(); event.stopImmediatePropagation();
        const server = JSON.parse(localStorage.getItem('jellyfin_credentials') || '{}').Servers?.[0];
        if (server?.AccessToken) await fetch('/Sessions/Logout', { method: 'POST', headers: { Authorization: `MediaBrowser Token="${server.AccessToken}"` } });
        localStorage.clear(); location.assign('/oidc/logout');
    }, true);
    fetch('/oidc/config').then(response => response.ok ? response.json() : null).then(config => {
        if (!config) return;
        window.oidcButtonText = config.LoginButtonText;
        window.oidcRpInitiatedLogout = config.RpInitiatedLogout;
        if (config.PasswordLoginMode === 'DisableForAllUsers' && !hasOidcError() && !sessionStorage.oidcStarted) {
            sessionStorage.oidcStarted = 'true'; location.assign(endpoint); return;
        }
        new MutationObserver(addLogin).observe(document.documentElement, { childList: true, subtree: true });
        addEventListener('hashchange', () => { addLogin(); showError(); });
        addLogin();
        showError();
        enableLogout();
    });
})();
