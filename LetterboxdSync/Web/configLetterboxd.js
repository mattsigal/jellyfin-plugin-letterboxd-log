
export const pluginId = 'b1fb3d98-3336-4b87-a5c9-8a948bd87233';

export default function (view, params) {

    function addWatchlistEntry(container, value) {
        var row = document.createElement('div');
        row.className = 'inputContainer';
        row.style.display = 'flex';
        row.style.alignItems = 'center';
        row.style.gap = '10px';

        var input = document.createElement('input');
        input.setAttribute('is', 'emby-input');
        input.type = 'text';
        input.className = 'watchlist-entry';
        input.setAttribute('label', 'Watchlist link or Letterboxd profile');
        input.setAttribute('autocomplete', 'off');
        input.value = value || '';
        input.style.flex = '1';

        var removeBtn = document.createElement('button');
        removeBtn.setAttribute('is', 'emby-button');
        removeBtn.type = 'button';
        removeBtn.className = 'raised';
        removeBtn.textContent = 'Remove';
        removeBtn.addEventListener('click', function () {
            row.remove();
        });

        row.appendChild(input);
        row.appendChild(removeBtn);
        container.appendChild(row);
    }

    function loadWatchlistForUser(accountData) {
        var container = view.querySelector('#watchlistContainer');
        container.innerHTML = '';
        var usernames = accountData.WatchlistUsernames || [];
        for (var i = 0; i < usernames.length; i++) {
            addWatchlistEntry(container, usernames[i]);
        }
    }

    function getWatchlistUsernames() {
        var watchlistInputs = view.querySelectorAll('.watchlist-entry');
        var usernames = [];
        for (var i = 0; i < watchlistInputs.length; i++) {
            var val = watchlistInputs[i].value.trim();
            if (val) {
                usernames.push(val);
            }
        }
        return usernames;
    }

    view.addEventListener('viewshow', function (e) {

        const selectUsers = view.querySelector('#usersJellyfin');
        selectUsers.innerHTML = '';

        ApiClient.getUsers().then(users => {
            for (let user of users){
                const option = document.createElement('option');
                option.value = user.Id;
                option.textContent = user.Name;
                selectUsers.appendChild(option);
            }

            const userSelectedId = document.getElementById('usersJellyfin').value;
            ApiClient.getPluginConfiguration(pluginId).then(config => {
                let configUserFilter = config.Accounts.filter(function (item) {
                    return item.UserJellyfin == userSelectedId;
                });
                if (configUserFilter.length > 0) {
                    view.querySelector('#lbxd-account').value = configUserFilter[0].UserLetterboxd;
                    view.querySelector('#lbxd-key').value = configUserFilter[0].PasswordLetterboxd;
                    view.querySelector('#cookiesraw').value = configUserFilter[0].CookiesRaw || '';
                    view.querySelector('#enable').checked = configUserFilter[0].Enable;
                    view.querySelector('#sendfavorite').checked = configUserFilter[0].SendFavorite;
                    view.querySelector('#enabledatefilter').checked = configUserFilter[0].EnableDateFilter || false;
                    view.querySelector('#datefilterdays').value = configUserFilter[0].DateFilterDays || 7;
                    loadWatchlistForUser(configUserFilter[0]);
                } else {
                    loadWatchlistForUser({});
                }
            });
        });
    });

    view.querySelector('#addWatchlistBtn').addEventListener('click', function () {
        var container = view.querySelector('#watchlistContainer');
        addWatchlistEntry(container, '');
    });

    view.querySelector('#usersJellyfin').addEventListener('change', function(e) {

        e.preventDefault();
        const userSelectedId = e.target.value;

        ApiClient.getPluginConfiguration(pluginId).then(config => {

            let configUserFilter = config.Accounts.filter(function (item) {
                return item.UserJellyfin == userSelectedId;
            });

            if (configUserFilter.length > 0) {
                view.querySelector('#lbxd-account').value = configUserFilter[0].UserLetterboxd;
                view.querySelector('#lbxd-key').value = configUserFilter[0].PasswordLetterboxd;
                view.querySelector('#cookiesraw').value = configUserFilter[0].CookiesRaw || '';
                view.querySelector('#enable').checked = configUserFilter[0].Enable;
                view.querySelector('#sendfavorite').checked = configUserFilter[0].SendFavorite;
                view.querySelector('#enabledatefilter').checked = configUserFilter[0].EnableDateFilter || false;
                view.querySelector('#datefilterdays').value = configUserFilter[0].DateFilterDays || 7;
                loadWatchlistForUser(configUserFilter[0]);
            }
            else {
                view.querySelector('#lbxd-account').value = '';
                view.querySelector('#lbxd-key').value = '';
                view.querySelector('#cookiesraw').value = '';
                view.querySelector('#enable').checked = false;
                view.querySelector('#sendfavorite').checked = false;
                view.querySelector('#enabledatefilter').checked = false;
                view.querySelector('#datefilterdays').value = 7;
                loadWatchlistForUser({});
            }

        });
    });

    view.querySelector('#LetterboxdSyncConfigForm').addEventListener('submit', function (e) {

        e.preventDefault();

        Dashboard.showLoadingMsg();

        const userSelectedId = document.getElementById('usersJellyfin').value;

        ApiClient.getPluginConfiguration(pluginId).then(config => {

            let AccountsUpdate = [];

            for (let account of config.Accounts)
                if (account.UserJellyfin != userSelectedId)
                    AccountsUpdate.push(account);

            let configUser = {};
            configUser.UserJellyfin = userSelectedId;
            configUser.UserLetterboxd = view.querySelector('#lbxd-account').value;
            configUser.PasswordLetterboxd = view.querySelector('#lbxd-key').value;
            configUser.CookiesRaw = view.querySelector('#cookiesraw').value;
            configUser.Enable = view.querySelector('#enable').checked;
            configUser.SendFavorite = view.querySelector('#sendfavorite').checked;
            configUser.EnableDateFilter = view.querySelector('#enabledatefilter').checked;
            configUser.DateFilterDays = parseInt(view.querySelector('#datefilterdays').value) || 7;
            configUser.WatchlistUsernames = getWatchlistUsernames();

            const data = JSON.stringify(configUser);
            const url = ApiClient.getUrl('Jellyfin.Plugin.LetterboxdSync/Authenticate');

            console.log(configUser);
            if (!configUser.Enable){
                Dashboard.hideLoadingMsg();

                AccountsUpdate.push(configUser);
                config.Accounts = AccountsUpdate;

                ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                    Dashboard.processPluginConfigurationUpdateResult(result);
                });
            }
            else {
                ApiClient.ajax({ type: 'POST', url, data, contentType: 'application/json'}).then(function (response) {

                    Dashboard.hideLoadingMsg();

                    AccountsUpdate.push(configUser);
                    config.Accounts = AccountsUpdate;

                    ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                        Dashboard.processPluginConfigurationUpdateResult(result);
                    });

                }).catch(function (response) {
                    response.json().then(res => {
                        console.log(res);
                        Dashboard.hideLoadingMsg();
                        Dashboard.processErrorResponse({statusText: `${response.statusText} - ${res.Message}`});
                    });
                });
            }
        })
    })
}
