(() => {
    const menuToggle = document.querySelector('.menu-toggle');
    menuToggle?.addEventListener('click', () => {
        const open = document.getElementById('home-navigation').classList.toggle('is-open');
        menuToggle.setAttribute('aria-expanded', String(open));
    });
    const money = new Intl.NumberFormat('vi-VN', { style: 'currency', currency: 'VND', maximumFractionDigits: 0 });
    const fallback = '/images/615d428f-d575-4362-9f21-fd32c6e44e88_phuquoc.webp';
    function element(tag, className, text) {
        const node = document.createElement(tag);
        node.className = className;
        if (text !== undefined) node.textContent = text;
        return node;
    }
    function imageUrl(value) {
        if (!value) return fallback;
        const path = value.replace(/\\/g, '/').replace(/^~\//, '/').replace(/^wwwroot\//, '/');
        const url = new URL(path.startsWith('/') || /^https?:\/\//i.test(path) ? path : '/images/' + path.replace(/^images\//, ''), location.origin);
        return ['http:', 'https:'].includes(url.protocol) ? url.href : fallback;
    }
    function card(tour) {
        const article = element('article', 'deal-card');
        const link = element('a', 'deal-image');
        link.href = '/Tours/Details/' + encodeURIComponent(tour.tourId);
        const image = element('img', '');
        image.src = imageUrl(tour.imageUrl); image.alt = tour.name; image.loading = 'lazy';
        image.addEventListener('error', () => { image.src = fallback; }, { once: true });
        link.append(image);
        const content = element('div', 'deal-content');
        const meta = element('div', 'deal-meta');
        const detail = element('a', 'deal-link', 'Xem tour →'); detail.href = link.href;
        meta.append(element('span', 'price new', money.format(tour.price)), detail);
        content.append(element('h3', '', tour.name), element('p', 'deal-location', tour.location), meta);
        article.append(link, content); return article;
    }
    let current = 0;
    function select(index) {
        const slides = [...document.querySelectorAll('.hero-slide')];
        current = (index + slides.length) % slides.length;
        slides.forEach((slide, i) => slide.classList.toggle('is-active', i === current));
        document.querySelectorAll('.hero-dot').forEach((dot, i) => {
            dot.classList.toggle('is-active', i === current); dot.setAttribute('aria-pressed', String(i === current));
        });
    }
    document.querySelector('.is-prev').addEventListener('click', () => select(current - 1));
    document.querySelector('.is-next').addEventListener('click', () => select(current + 1));
    async function loadTours() {
        const status = document.getElementById('tour-status');
        try {
            const response = await fetch('/api/tours', { signal: AbortSignal.timeout(8000) });
            if (!response.ok) throw new Error('Catalog unavailable');
            const tours = await response.json();
            if (!Array.isArray(tours)) throw new Error('Invalid tours');
            if (!tours.length) { status.textContent = 'Các hành trình mới đang được cập nhật. Bạn có thể liên hệ để được tư vấn.'; return; }
            const ordered = [...tours].sort((a, b) => a.price - b.price);
            document.getElementById('today-tours').replaceChildren(...ordered.slice(0, 3).map(card));
            document.getElementById('monthly-tours').replaceChildren(...tours.slice(-3).reverse().map(card));
            document.getElementById('monthly-section').hidden = false; status.hidden = true;
            const track = document.getElementById('hero-track');
            tours.slice(0, 3).forEach(tour => {
                const slide = element('article', 'hero-slide');
                slide.style.backgroundImage = `url(${JSON.stringify(imageUrl(tour.imageUrl))})`;
                const content = element('div', 'home-container hero-content');
                const actions = element('div', 'hero-actions');
                const book = element('a', 'btn-hero primary', 'Khám phá tour →');
                book.href = '/Tours/Details/' + encodeURIComponent(tour.tourId);
                const contact = element('a', 'btn-hero dark', 'Liên hệ tư vấn'); contact.href = '/Home/Contact';
                actions.append(book, contact);
                content.append(element('span', 'hero-tag', 'Hành trình dành cho bạn'), element('h2', '', tour.name), element('p', 'hero-price', 'Giá từ ' + money.format(tour.price)), element('p', 'hero-desc is-clamped', tour.description), actions);
                slide.append(element('div', 'hero-overlay'), content); track.append(slide);
            });
            document.querySelectorAll('.hero-slide').forEach((_, index) => {
                const dot = element('button', 'hero-dot'); dot.type = 'button'; dot.setAttribute('aria-label', 'Hành trình ' + (index + 1));
                dot.addEventListener('click', () => select(index)); document.querySelector('.hero-dots').append(dot);
            });
            select(0);
        } catch {
            status.textContent = 'Danh sách tour tạm thời chưa tải được. Vui lòng thử lại sau hoặc liên hệ để được tư vấn.';
        }
    }
    async function loadAccount() {
        try {
            const response = await fetch('/api/identity/me', { signal: AbortSignal.timeout(5000) });
            if (!response.ok) return;
            const account = await response.json();
            document.getElementById('reviews-nav-link').hidden = false;
            document.getElementById('admin-nav-link').hidden = !account.roles?.includes('Admin');
            const menu = element('details', 'user-menu');
            const avatar = element('summary', 'user-avatar', (account.name || 'U').slice(0, 1).toUpperCase());
            avatar.setAttribute('aria-label', 'Mở menu tài khoản');
            const dropdown = element('div', 'account-dropdown');
            dropdown.append(element('div', 'account-name', account.name || 'Người dùng'));
            const links = [['Hồ sơ', '/Account/Profile'], ['Tour của tôi', '/Account/Bookings'], ['Thông báo', '/Notifications'], ['Đánh giá của tôi', '/Account/Reviews']];
            if (account.roles?.includes('Admin')) links.unshift(['Dashboard Admin', '/Admin/Dashboard']);
            links.forEach(([label, href]) => { const link = element('a', '', label); link.href = href; dropdown.append(link); });
            const logout = element('form', 'account-logout'); logout.action = '/Account/Logout'; logout.method = 'post';
            const button = element('button', '', 'Đăng xuất'); button.type = 'submit'; logout.append(button);
            logout.addEventListener('submit', async event => {
                if (logout.querySelector('input[name="__RequestVerificationToken"]')) return;
                event.preventDefault(); button.disabled = true;
                try {
                    const profile = await fetch('/Account/Profile', { signal: AbortSignal.timeout(8000) });
                    if (!profile.ok) throw new Error('Cannot load logout token');
                    const doc = new DOMParser().parseFromString(await profile.text(), 'text/html');
                    const token = doc.querySelector('form[action="/Account/Logout"] input[name="__RequestVerificationToken"]');
                    if (!token) throw new Error('Missing logout token');
                    logout.append(document.importNode(token, true)); logout.requestSubmit();
                } catch { button.disabled = false; button.textContent = 'Thử đăng xuất lại'; }
            });
            dropdown.append(logout); menu.append(avatar, dropdown);
            document.querySelector('.header-account').replaceChildren(menu);
            document.addEventListener('click', event => { if (!menu.contains(event.target)) menu.open = false; });
            document.addEventListener('keydown', event => { if (event.key === 'Escape') { menu.open = false; avatar.focus(); } });
        } catch { /* Public navigation remains usable when Identity is unavailable. */ }
    }
    loadTours(); loadAccount();
})();
