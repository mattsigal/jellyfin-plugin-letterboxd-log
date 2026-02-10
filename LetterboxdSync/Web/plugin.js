(function () {
    'use strict';

    var MENU_ID = 'letterboxdSyncMenuSection';
    var PAGE_URL = '#!/configurationpage?name=userConfigLetterboxd';

    function injectSidebarEntry() {
        if (document.getElementById(MENU_ID)) {
            return;
        }

        var scrollContainer = document.querySelector('.mainDrawer-scrollContainer');
        if (!scrollContainer) {
            return;
        }

        var section = document.createElement('div');
        section.id = MENU_ID;
        section.className = 'navMenuSection';

        var header = document.createElement('h3');
        header.className = 'sidebarHeader';
        header.textContent = 'Letterboxd';
        section.appendChild(header);

        var link = document.createElement('a');
        link.setAttribute('is', 'emby-linkbutton');
        link.className = 'navMenuOption lnkMediaFolder emby-button';
        link.href = PAGE_URL;
        link.innerHTML = '<span class="material-icons navMenuOptionIcon" aria-hidden="true">movie_filter</span>' +
            '<span class="navMenuOptionText">Letterboxd Sync</span>';
        section.appendChild(link);

        // Insert before admin section if present, otherwise append at end of sidebar
        var adminSection = scrollContainer.querySelector('.adminMenuOptions');
        if (adminSection) {
            adminSection.parentNode.insertBefore(section, adminSection);
        } else {
            scrollContainer.appendChild(section);
        }
    }

    var observer = new MutationObserver(function () {
        injectSidebarEntry();
    });

    observer.observe(document.body, {
        childList: true,
        subtree: true
    });

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', injectSidebarEntry);
    } else {
        injectSidebarEntry();
    }
})();
