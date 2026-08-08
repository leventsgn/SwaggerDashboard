window.swaggerDashboard = {
    setTheme: function (theme) {
        document.documentElement.setAttribute('data-bs-theme', theme);
        try {
            localStorage.setItem('swagger-dashboard-theme', theme);
        } catch (e) {
            // Storage may be unavailable in private mode; the theme still applies for this page.
        }
    },

    getTheme: function () {
        return document.documentElement.getAttribute('data-bs-theme') || 'light';
    },

    copyToClipboard: async function (text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch (e) {
            return false;
        }
    },

    downloadBytes: function (fileName, contentType, base64) {
        const link = document.createElement('a');
        link.href = 'data:' + contentType + ';base64,' + base64;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
    },

    // HTML responses are shown inside a sandboxed frame with no script execution and no
    // same-origin access, so a hostile API response cannot reach the dashboard session.
    renderSandboxed: function (elementId, html) {
        const host = document.getElementById(elementId);
        if (!host) {
            return;
        }

        host.innerHTML = '';
        const frame = document.createElement('iframe');
        frame.setAttribute('sandbox', '');
        frame.setAttribute('class', 'response-sandbox');
        frame.srcdoc = html;
        host.appendChild(frame);
    }
};
