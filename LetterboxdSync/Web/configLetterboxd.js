
export const pluginId = 'c2fb3d98-3336-4b87-a5c9-8a948bd87234';

export default function (view, params) {

    view.addEventListener('viewshow', function (e) {

        const selectUsers = view.querySelector('#usersJellyfin');
        selectUsers.innerHTML = '';

        ApiClient.getUsers().then(users => {
            for (let user of users) {
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
                view.querySelector('#username').value = configUserFilter[0].UserLetterboxd;
                view.querySelector('#password').value = configUserFilter[0].PasswordLetterboxd;
                view.querySelector('#enable').checked = configUserFilter[0].Enable;
                view.querySelector('#sendfavorite').checked = configUserFilter[0].SendFavorite;
                view.querySelector('#enabledatefilter').checked = configUserFilter[0].EnableDateFilter || false;
                view.querySelector('#datefilterdays').value = configUserFilter[0].DateFilterDays || 7;
                view.querySelector('#timezoneoffset').value = configUserFilter[0].TimezoneOffset || 0;
                view.querySelector('#cookie').value = configUserFilter[0].Cookie || '';
                view.querySelector('#useragent').value = configUserFilter[0].UserAgent || '';
                view.querySelector('#cookiesraw').value = configUserFilter[0].CookiesRaw || '';
                view.querySelector('#cookiesuseragent').value = configUserFilter[0].CookiesUserAgent || '';
            });
        });
    });


    view.querySelector('#usersJellyfin').addEventListener('change', function (e) {

        e.preventDefault();
        const userSelectedId = e.target.value;

        ApiClient.getPluginConfiguration(pluginId).then(config => {

            let configUserFilter = config.Accounts.filter(function (item) {
                return item.UserJellyfin == userSelectedId;
            });

            if (configUserFilter.length > 0) {
                view.querySelector('#username').value = configUserFilter[0].UserLetterboxd;
                view.querySelector('#password').value = configUserFilter[0].PasswordLetterboxd;
                view.querySelector('#enable').checked = configUserFilter[0].Enable;
                view.querySelector('#sendfavorite').checked = configUserFilter[0].SendFavorite;
                view.querySelector('#enabledatefilter').checked = configUserFilter[0].EnableDateFilter || false;
                view.querySelector('#datefilterdays').value = configUserFilter[0].DateFilterDays || 7;
                view.querySelector('#timezoneoffset').value = configUserFilter[0].TimezoneOffset || 0;
                view.querySelector('#cookie').value = configUserFilter[0].Cookie || '';
                view.querySelector('#useragent').value = configUserFilter[0].UserAgent || '';
                view.querySelector('#cookiesraw').value = configUserFilter[0].CookiesRaw || '';
                view.querySelector('#cookiesuseragent').value = configUserFilter[0].CookiesUserAgent || '';
            }
            else {
                view.querySelector('#username').value = '';
                view.querySelector('#password').value = '';
                view.querySelector('#enable').checked = false;
                view.querySelector('#sendfavorite').checked = false;
                view.querySelector('#enabledatefilter').checked = false;
                view.querySelector('#datefilterdays').value = 7;
                view.querySelector('#timezoneoffset').value = 0;
                view.querySelector('#cookie').value = '';
                view.querySelector('#useragent').value = '';
                view.querySelector('#cookiesraw').value = '';
                view.querySelector('#cookiesuseragent').value = '';
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
            configUser.UserLetterboxd = view.querySelector('#username').value;
            configUser.PasswordLetterboxd = view.querySelector('#password').value;
            configUser.Enable = view.querySelector('#enable').checked;
            configUser.SendFavorite = view.querySelector('#sendfavorite').checked;
            configUser.EnableDateFilter = view.querySelector('#enabledatefilter').checked;
            configUser.DateFilterDays = parseInt(view.querySelector('#datefilterdays').value) || 7;
            configUser.TimezoneOffset = parseInt(view.querySelector('#timezoneoffset').value) || 0;
            configUser.Cookie = view.querySelector('#cookie').value;
            configUser.UserAgent = view.querySelector('#useragent').value;
            configUser.CookiesRaw = view.querySelector('#cookiesraw').value;
            configUser.CookiesUserAgent = view.querySelector('#cookiesuseragent').value;

            const data = JSON.stringify(configUser);
            const url = ApiClient.getUrl('Jellyfin.Plugin.LetterboxdLog/Authenticate');

            console.log(configUser);
            if (!configUser.Enable) {
                Dashboard.hideLoadingMsg();

                AccountsUpdate.push(configUser);
                config.Accounts = AccountsUpdate;

                ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                    Dashboard.processPluginConfigurationUpdateResult(result);
                });
            }
            else {
                ApiClient.ajax({ type: 'POST', url, data, contentType: 'application/json' }).then(function (response) {

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
                        Dashboard.processErrorResponse({ statusText: `${response.statusText} - ${res.Message}` });
                    });
                });
            }
        })
    })
}
