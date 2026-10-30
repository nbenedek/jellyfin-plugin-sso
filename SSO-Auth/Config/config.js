const ssoConfigurationPage = {
  pluginUniqueId: "505ce9d1-d916-42fa-86ca-673ef241d7df",
  loadConfiguration: (page) => {
    ApiClient.getPluginConfiguration(ssoConfigurationPage.pluginUniqueId).then(
      (config) => {
        ssoConfigurationPage.populateProviders(page, config.OidConfigs);
        ssoConfigurationPage.populateLinkProviders(page, config.OidConfigs);
      },
    );

    const folder_container = page.querySelector("#EnabledFolders");
    ssoConfigurationPage.populateFolders(folder_container);
  },
  populateProviders: (page, providers) => {
    // Clear providers in case there are out of date ones
    page
      .querySelector("#selectProvider")
      .querySelectorAll("option")
      .forEach((option) => {
        option.remove();
      });

    // Add providers as options for the selector

    Object.keys(providers).forEach((provider_name) => {
      var choice = new Option(provider_name, provider_name);

      page.querySelector("#selectProvider").appendChild(choice);
    });
  },
  populateEnabledFolders: (folder_list, container) => {
    container.querySelectorAll(".folder-checkbox").forEach((e) => {
      e.checked = folder_list.includes(e.getAttribute("data-id"));
    });
  },
  serializeEnabledFolders: (container) => {
    return [...container.querySelectorAll(".folder-checkbox")]
      .filter((e) => e.checked)
      .map((e) => {
        return e.getAttribute("data-id");
      });
  },
  populateFolders: (container) => {
    return ApiClient.getJSON(
      ApiClient.getUrl("Library/MediaFolders", {
        IsHidden: false,
      }),
    ).then((folders) => {
      ssoConfigurationPage._populateFolders(container, folders);
    });
  },
  /*
  container: html element
  folders.Items: array of objects, with .Id & .Name
  */
  _populateFolders: (container, folders) => {
    container
      .querySelectorAll(".emby-checkbox-label")
      .forEach((e) => e.remove());

    const checkboxes = folders.Items.map((folder) => {
      var out = document.createElement("label");

      out.innerHTML = `
        <input
          is="emby-checkbox"
          class="folder-checkbox chkFolder"
          data-id="${folder.Id}"
          type="checkbox"
        />
        <span>${folder.Name}</span>
      `;

      return out;
    });

    checkboxes.forEach((e) => {
      container.appendChild(e);
    });
  },

  populateRoleMappings: (folder_role_mappings, container) => {
    container
      .querySelectorAll(".sso-role-mapping-container")
      .forEach((e) => e.remove());

    const mapping_elements = folder_role_mappings.map((mapping) => {
      var elem = document.createElement("div");

      elem.classList.add("sso-role-mapping-container");
      elem.innerHTML = `
      <label
        class="inputLabel inputLabelUnfocused sso-role-mapping-input-label" 
      >Role:</label>
      <div class="listItem">
        <input
          is="emby-input"
          required=""
          type="text"
          class="listItemBody sso-role-mapping-name"
        />
        <button
          type="button"
          is="paper-icon-button-light"
          class="listItemButton sso-remove-role-mapping"
        >
          <span class="material-icons remove_circle" aria-hidden="true"></span>
        </button> 
      </div> 
      <div
        class="checkboxList paperList sso-folder-list"
      ></div>
      `;

      var checklist = elem.querySelector(".sso-folder-list");
      const enabled_folders = mapping["Folders"];

      ssoConfigurationPage
        .populateFolders(checklist)
        .then(() =>
          ssoConfigurationPage.populateEnabledFolders(
            enabled_folders,
            checklist,
          ),
        );

      elem.querySelector(".sso-role-mapping-name").value = mapping["Role"];
      elem
        .querySelector(".sso-remove-role-mapping")
        .addEventListener(
          "click",
          ssoConfigurationPage.handleRoleMappingRemove,
        );

      return elem;
    });

    mapping_elements.forEach((e) => container.appendChild(e));
  },
  serializeRoleMappings: (container) => {
    var out = [];
    const roles = [
      ...container.querySelectorAll(".sso-role-mapping-container"),
    ].forEach((elem) => {
      const role = elem.querySelector(".sso-role-mapping-name").value;
      const checklist = elem.querySelector(".sso-folder-list");

      out.push({
        Role: role,
        Folders: ssoConfigurationPage.serializeEnabledFolders(checklist),
      });
    });

    return out;
  },
  handleRoleMappingRemove: (evt) => {
    const targeted_mapping = evt.target.closest(".sso-role-mapping-container");
    targeted_mapping.remove();
  },
  listArgumentsByType: (page) => {
    const json_class = ".sso-json";
    const toggle_class = ".sso-toggle";
    const text_class = ".sso-text";
    const text_list_class = ".sso-line-list";

    const folder_list_fields = ["EnabledFolders"];
    const role_map_fields = ["FolderRoleMapping"];

    const oidc_form = page.querySelector("#sso-new-oidc-provider");

    const text_fields = [...oidc_form.querySelectorAll(text_class)].map(
      (e) => e.id,
    );

    const json_fields = [...oidc_form.querySelectorAll(json_class)].map(
      (e) => e.id,
    );

    const text_list_fields = [
      ...oidc_form.querySelectorAll(text_list_class),
    ].map((e) => e.id);

    const check_fields = [...oidc_form.querySelectorAll(toggle_class)].map(
      (e) => e.id,
    );

    const output = {
      json_fields,
      text_list_fields,
      text_fields,
      check_fields,
      folder_list_fields,
      role_map_fields,
    };

    return output;
  },
  fillTextList: (text_list, element) => {
    // text_list is an array of strings
    // element is an input element
    const val = text_list.join("\r\n");
    element.value = val;
  },
  parseTextList: (element) => {
    // Return the parsed text list
    var out = element.value
      .split("\n")
      .map((e) => e.trim())
      .filter((e) => e);
    return out;
  },
  loadProvider: (page, provider_name) => {
    ApiClient.getPluginConfiguration(ssoConfigurationPage.pluginUniqueId).then(
      (config) => {
        var provider = config.OidConfigs[provider_name] || {};

        const form_elements = ssoConfigurationPage.listArgumentsByType(page);

        page.querySelector("#OidProviderName").value = provider_name;

        form_elements.text_fields.forEach((id) => {
          if (provider[id]) page.querySelector("#" + id).value = provider[id];
        });

        form_elements.json_fields.forEach((id) => {
          if (provider[id])
            page.querySelector("#" + id).value = JSON.stringify(provider[id]);
        });

        form_elements.text_list_fields.forEach((id) => {
          if (provider[id])
            ssoConfigurationPage.fillTextList(
              provider[id],
              page.querySelector("#" + id),
            );
        });

        form_elements.folder_list_fields.forEach((id) => {
          if (provider[id]) {
            ssoConfigurationPage.populateEnabledFolders(
              provider[id],
              page.querySelector(`#${id}`),
            );
          }
        });

        form_elements.check_fields.forEach((id) => {
          if (provider[id]) page.querySelector("#" + id).checked = provider[id];
        });

        form_elements.role_map_fields.forEach((id) => {
          const elem = page.querySelector(`#${id}`);
          if (provider[id])
            ssoConfigurationPage.populateRoleMappings(provider[id], elem);
        });
      },
    );
  },
  deleteProvider: (page, provider_name) => {
    if (
      !window.confirm(
        `Are you sure you want to delete the provider ${provider_name}?`,
      )
    ) {
      return;
    }
    return new Promise((resolve) => {
      ApiClient.getPluginConfiguration(
        ssoConfigurationPage.pluginUniqueId,
      ).then((config) => {
        if (!config.OidConfigs.hasOwnProperty(provider_name)) {
          resolve();
          return;
        }

        delete config.OidConfigs[provider_name];
        ApiClient.updatePluginConfiguration(
          ssoConfigurationPage.pluginUniqueId,
          config,
        ).then(function (result) {
          Dashboard.processPluginConfigurationUpdateResult(result);
          ssoConfigurationPage.loadConfiguration(page);

          Dashboard.alert("Provider removed");

          resolve();
        });
      });
    });
  },
  saveProvider: (page, provider_name) => {
    return new Promise((resolve) => {
      const form_elements = ssoConfigurationPage.listArgumentsByType(page);

      ApiClient.getPluginConfiguration(
        ssoConfigurationPage.pluginUniqueId,
      ).then((config) => {
        var current_config = {};
        if (config.OidConfigs.hasOwnProperty(provider_name)) {
          current_config = config.OidConfigs[provider_name];
        }

        form_elements.text_fields.forEach((id) => {
          const value = page.querySelector("#" + id).value;
          if (value) {
            current_config[id] = page.querySelector("#" + id).value;
          } else {
            current_config[id] = null;
          }
        });

        form_elements.json_fields.forEach((id) => {
          const value = page.querySelector("#" + id).value;
          if (value) {
            current_config[id] = JSON.parse(value);
          } else {
            current_config[id] = null;
          }
        });

        form_elements.check_fields.forEach((id) => {
          current_config[id] = page.querySelector("#" + id).checked;
        });

        form_elements.text_list_fields.forEach((id) => {
          current_config[id] = ssoConfigurationPage.parseTextList(
            page.querySelector("#" + id),
          );
        });

        form_elements.folder_list_fields.forEach((id) => {
          const elem = page.querySelector(`#${id}`);
          current_config[id] =
            ssoConfigurationPage.serializeEnabledFolders(elem);
        });

        form_elements.role_map_fields.forEach((id) => {
          const elem = page.querySelector(`#${id}`);
          current_config[id] = ssoConfigurationPage.serializeRoleMappings(elem);
        });

        config.OidConfigs[provider_name] = current_config;

        ApiClient.updatePluginConfiguration(
          ssoConfigurationPage.pluginUniqueId,
          config,
        ).then(function (result) {
          Dashboard.processPluginConfigurationUpdateResult(result);
          ssoConfigurationPage.loadConfiguration(page);
          ssoConfigurationPage.loadProvider(page, provider_name);

          page.querySelector("#selectProvider").value = provider_name;
          Dashboard.alert("Settings saved.");
          resolve();
        });
      });
    });
  },
  populateLinkProviders: (page, providers) => {
    const select = page.querySelector("#sso-link-provider");
    const selected = select.value;
    select.replaceChildren();
    Object.keys(providers).forEach((name) => {
      select.add(new Option(name, name));
    });
    if (Object.hasOwn(providers, selected)) {
      select.value = selected;
    }
    ssoConfigurationPage.populateLinkIssuer(page, providers);
  },
  populateLinkIssuer: (page, providers) => {
    const provider = providers[page.querySelector("#sso-link-provider").value];
    page.querySelector("#sso-link-issuer").value =
      provider?.OidEndpoint?.trim() || "";
  },
  setLinkStatus: (page, message) => {
    page.querySelector("#sso-admin-link-status").textContent = message;
  },
  linkRequest: (options) => ApiClient.fetch(options),
  createIdentityLink: async (page) => {
    const provider = page.querySelector("#sso-link-provider").value;
    const userId = page.querySelector("#sso-link-user-id").value.trim();
    const issuer = page.querySelector("#sso-link-issuer").value.trim();
    const subject = page.querySelector("#sso-link-subject").value.trim();
    const username = page.querySelector("#sso-link-username").value.trim();

    if (
      !provider ||
      !/^[a-f0-9-]{36}$/i.test(userId) ||
      !issuer ||
      !subject ||
      !username
    ) {
      ssoConfigurationPage.setLinkStatus(
        page,
        "Enter a provider, valid Jellyfin user ID, issuer, subject, and username.",
      );
      return;
    }
    if (
      !window.confirm(
        `Link Keycloak subject "${subject}" to Jellyfin user "${userId}"?`,
      )
    ) {
      return;
    }

    ssoConfigurationPage.setLinkStatus(page, "Saving identity link...");
    try {
      const response = await ssoConfigurationPage.linkRequest({
        type: "POST",
        url: ApiClient.getUrl(
          `sso/oid/Link/${encodeURIComponent(provider)}/${userId}`,
        ),
        contentType: "application/json",
        data: JSON.stringify({
          Issuer: issuer,
          Subject: subject,
          Username: username,
        }),
      });
      if (!response.ok) {
        throw new Error(`Server returned HTTP ${response.status}`);
      }
      ssoConfigurationPage.setLinkStatus(page, "Identity link saved.");
      await ssoConfigurationPage.listIdentityLinks(page);
    } catch (error) {
      ssoConfigurationPage.setLinkStatus(page, `Link failed: ${error.message}`);
    }
  },
  listIdentityLinks: async (page) => {
    const userId = page.querySelector("#sso-link-user-id").value.trim();
    const results = page.querySelector("#sso-admin-link-results");
    results.replaceChildren();
    if (!/^[a-f0-9-]{36}$/i.test(userId)) {
      ssoConfigurationPage.setLinkStatus(
        page,
        "Enter a valid Jellyfin user ID to list links.",
      );
      return;
    }
    try {
      const response = await ssoConfigurationPage.linkRequest({
        type: "GET",
        url: ApiClient.getUrl(`sso/oid/links/${userId}`),
      });
      if (!response.ok) {
        throw new Error(`Server returned HTTP ${response.status}`);
      }
      const providers = await response.json();
      for (const [provider, names] of Object.entries(providers)) {
        for (const name of names) {
          const row = document.createElement("div");
          row.className = "listItem";
          const label = document.createElement("span");
          label.className = "listItemBody";
          label.textContent = `${provider}: ${name}`;
          const button = document.createElement("button");
          button.type = "button";
          button.className = "raised emby-button";
          button.textContent = "Unlink";
          button.addEventListener("click", async () => {
            if (
              !window.confirm(
                `Remove "${name}" from Jellyfin user "${userId}"?`,
              )
            ) {
              return;
            }
            try {
              const response = await ssoConfigurationPage.linkRequest({
                type: "DELETE",
                url: ApiClient.getUrl(
                  `sso/oid/Link/${encodeURIComponent(provider)}/${userId}/${encodeURIComponent(name)}`,
                ),
              });
              if (!response.ok) {
                throw new Error(`Server returned HTTP ${response.status}`);
              }
              ssoConfigurationPage.setLinkStatus(
                page,
                "Identity link removed.",
              );
              await ssoConfigurationPage.listIdentityLinks(page);
            } catch (error) {
              ssoConfigurationPage.setLinkStatus(
                page,
                `Unlink failed: ${error.message}`,
              );
            }
          });
          row.append(label, button);
          results.appendChild(row);
        }
      }
      if (!results.children.length) {
        ssoConfigurationPage.setLinkStatus(
          page,
          "No identity links for this Jellyfin user.",
        );
      }
    } catch (error) {
      ssoConfigurationPage.setLinkStatus(
        page,
        `Could not load links: ${error.message}`,
      );
    }
  },
  addTextAreaStyle: (view) => {
    var style = document.createElement("link");
    style.rel = "stylesheet";
    style.href =
      ApiClient.getUrl("web/configurationpage") + "?name=SSO-Auth.css";
    view.appendChild(style);
  },
};

export default function (view) {
  ssoConfigurationPage.addTextAreaStyle(view);
  ssoConfigurationPage.loadConfiguration(view);

  ssoConfigurationPage.listArgumentsByType(view);

  view.querySelector("#SaveProvider").addEventListener("click", (e) => {
    const target_provider = view.querySelector("#OidProviderName").value;

    ssoConfigurationPage.saveProvider(view, target_provider);

    e.preventDefault();
    return false;
  });

  view.querySelector("#LoadProvider").addEventListener("click", (e) => {
    const target_provider = view.querySelector("#selectProvider").value;

    ssoConfigurationPage.loadProvider(view, target_provider);

    e.preventDefault();
    return false;
  });

  view.querySelector("#DeleteProvider").addEventListener("click", (e) => {
    const target_provider = view.querySelector("#selectProvider").value;

    ssoConfigurationPage.deleteProvider(view, target_provider);

    e.preventDefault();
    return false;
  });

  view.querySelector("#AddRoleMapping").addEventListener("click", (e) => {
    const container = view.querySelector("#FolderRoleMapping");
    const current_mappings =
      ssoConfigurationPage.serializeRoleMappings(container);
    current_mappings.push({ Role: "", Folders: [] });
    console.log(current_mappings);
    ssoConfigurationPage.populateRoleMappings(current_mappings, container);
  });

  view.querySelector("#sso-link-provider").addEventListener("change", () => {
    ApiClient.getPluginConfiguration(ssoConfigurationPage.pluginUniqueId).then(
      (config) =>
        ssoConfigurationPage.populateLinkIssuer(view, config.OidConfigs),
    );
  });
  view
    .querySelector("#sso-admin-link-add")
    .addEventListener("click", () =>
      ssoConfigurationPage.createIdentityLink(view),
    );
  view
    .querySelector("#sso-admin-link-list")
    .addEventListener("click", () =>
      ssoConfigurationPage.listIdentityLinks(view),
    );
}
