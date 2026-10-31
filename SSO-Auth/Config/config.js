const ssoConfigurationPage = {
  pluginUniqueId: "505ce9d1-d916-42fa-86ca-673ef241d7df",
  loadConfiguration: async (page) => {
    try {
      const config = await ApiClient.getPluginConfiguration(
        ssoConfigurationPage.pluginUniqueId,
      );
      ssoConfigurationPage.populateProviders(page, config.OidConfigs || {});
      ssoConfigurationPage.populateLinkProviders(page, config.OidConfigs || {});
      await ssoConfigurationPage.populateFolders(
        page.querySelector("#EnabledFolders"),
      );
    } catch (error) {
      ssoConfigurationPage.setProviderFeedback(
        page,
        "Could not load SSO provider settings: " + (error?.message || error),
        true,
      );
    }
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
    const allowed = Array.isArray(folder_list) ? folder_list : [];
    container.querySelectorAll(".folder-checkbox").forEach((e) => {
      e.checked = allowed.includes(e.getAttribute("data-id"));
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
    container.replaceChildren();

    // Never insert administrator-controlled library names through innerHTML.
    const checkboxes = (folders.Items || []).map((folder) => {
      const out = document.createElement("label");
      const input = document.createElement("input");
      input.setAttribute("is", "emby-checkbox");
      input.className = "folder-checkbox chkFolder";
      input.setAttribute("data-id", folder.Id);
      input.type = "checkbox";
      const name = document.createElement("span");
      name.textContent = folder.Name;
      out.append(input, name);
      return out;
    });

    checkboxes.forEach((e) => {
      container.appendChild(e);
    });
  },

  populateRoleMappings: async (folder_role_mappings, container) => {
    container
      .querySelectorAll(".sso-role-mapping-container")
      .forEach((e) => e.remove());

    const pending = [];
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
      const enabled_folders = mapping["Folders"] || [];

      pending.push(
        ssoConfigurationPage
          .populateFolders(checklist)
          .then(() =>
            ssoConfigurationPage.populateEnabledFolders(
              enabled_folders,
              checklist,
            ),
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
    await Promise.all(pending);
  },
  serializeRoleMappings: (container) => {
    var out = [];
    [...container.querySelectorAll(".sso-role-mapping-container")].forEach(
      (elem) => {
        const role = elem.querySelector(".sso-role-mapping-name").value;
        const checklist = elem.querySelector(".sso-folder-list");

        out.push({
          Role: role,
          Folders: ssoConfigurationPage.serializeEnabledFolders(checklist),
        });
      },
    );

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

    const text_fields = [...oidc_form.querySelectorAll(text_class)]
      .map((e) => e.id)
      .filter((id) => id !== "OidProviderName");

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
  setProviderFeedback: (page, message, isError = false) => {
    const status = page.querySelector("#sso-provider-feedback");
    status.textContent = message;
    status.classList.toggle("sso-feedback-error", isError);
  },
  resetProviderForm: (page) => {
    const formElements = ssoConfigurationPage.listArgumentsByType(page);
    const form = page.querySelector("#sso-new-oidc-provider");
    page.querySelector("#OidProviderName").value = "";
    formElements.text_fields.forEach((id) => {
      page.querySelector("#" + id).value = "";
    });
    formElements.json_fields.forEach((id) => {
      page.querySelector("#" + id).value = "";
    });
    formElements.text_list_fields.forEach((id) => {
      page.querySelector("#" + id).value = "";
    });
    formElements.check_fields.forEach((id) => {
      page.querySelector("#" + id).checked = false;
    });
    formElements.folder_list_fields.forEach((id) => {
      form.querySelectorAll("#" + id + " .folder-checkbox").forEach((e) => {
        e.checked = false;
      });
    });
    formElements.role_map_fields.forEach((id) => {
      page.querySelector("#" + id).replaceChildren();
    });
    page.dataset.ssoLoadedProvider = "";
    ssoConfigurationPage.setSecretVisible(page, false);
    ssoConfigurationPage.setProviderFeedback(page, "");
  },
  setSecretVisible: (page, visible) => {
    page.querySelector("#OidSecret").type = visible ? "text" : "password";
    const control = page.querySelector("#ToggleSecret");
    control.setAttribute("aria-pressed", String(visible));
    control.textContent = visible ? "Hide secret" : "Show secret";
  },
  loadProvider: async (page, providerName) => {
    if (!providerName) {
      ssoConfigurationPage.setProviderFeedback(
        page,
        "Choose a provider before loading it.",
        true,
      );
      return;
    }

    try {
      const config = await ApiClient.getPluginConfiguration(
        ssoConfigurationPage.pluginUniqueId,
      );
      const provider = config.OidConfigs?.[providerName];
      if (!provider) {
        throw new Error("The selected provider no longer exists.");
      }

      // Reset every field: unchecked booleans and empty text must not retain
      // values from the previously loaded provider.
      ssoConfigurationPage.resetProviderForm(page);
      await ssoConfigurationPage.populateFolders(
        page.querySelector("#EnabledFolders"),
      );
      const fields = ssoConfigurationPage.listArgumentsByType(page);
      page.querySelector("#OidProviderName").value = providerName;

      fields.text_fields.forEach((id) => {
        page.querySelector("#" + id).value = provider[id] ?? "";
      });
      fields.json_fields.forEach((id) => {
        page.querySelector("#" + id).value =
          provider[id] == null ? "" : JSON.stringify(provider[id]);
      });
      fields.text_list_fields.forEach((id) => {
        ssoConfigurationPage.fillTextList(
          Array.isArray(provider[id]) ? provider[id] : [],
          page.querySelector("#" + id),
        );
      });
      fields.folder_list_fields.forEach((id) => {
        ssoConfigurationPage.populateEnabledFolders(
          provider[id],
          page.querySelector("#" + id),
        );
      });
      fields.check_fields.forEach((id) => {
        page.querySelector("#" + id).checked = provider[id] === true;
      });
      await Promise.all(
        fields.role_map_fields.map((id) =>
          ssoConfigurationPage.populateRoleMappings(
            Array.isArray(provider[id]) ? provider[id] : [],
            page.querySelector("#" + id),
          ),
        ),
      );

      page.dataset.ssoLoadedProvider = providerName;
      ssoConfigurationPage.setProviderFeedback(
        page,
        'Loaded provider "' + providerName + '".',
      );
    } catch (error) {
      ssoConfigurationPage.setProviderFeedback(
        page,
        "Unable to load provider: " + (error?.message || error),
        true,
      );
    }
  },
  deleteProvider: async (page, providerName) => {
    if (!providerName) {
      ssoConfigurationPage.setProviderFeedback(
        page,
        "Select a provider first.",
        true,
      );
      return;
    }

    // Removal also deletes its saved OIDC subject links; require typing the
    // exact name instead of a yes/no click.
    const confirmation = window.prompt(
      'Deleting "' +
        providerName +
        '" also deletes its identity mappings. Type the provider name to confirm:',
    );
    if (confirmation !== providerName) {
      return;
    }

    try {
      const config = await ApiClient.getPluginConfiguration(
        ssoConfigurationPage.pluginUniqueId,
      );
      if (!Object.hasOwn(config.OidConfigs || {}, providerName)) {
        throw new Error("Provider no longer exists.");
      }

      delete config.OidConfigs[providerName];
      const result = await ApiClient.updatePluginConfiguration(
        ssoConfigurationPage.pluginUniqueId,
        config,
      );
      Dashboard.processPluginConfigurationUpdateResult(result);
      ssoConfigurationPage.resetProviderForm(page);
      await ssoConfigurationPage.loadConfiguration(page);
      ssoConfigurationPage.setProviderFeedback(page, "Provider removed.");
    } catch (error) {
      ssoConfigurationPage.setProviderFeedback(
        page,
        "Deletion failed: " + (error?.message || error),
        true,
      );
    }
  },
  validateProvider: (page, providerName, existing) => {
    const issuer = page.querySelector("#OidEndpoint").value.trim();
    const clientId = page.querySelector("#OidClientId").value.trim();
    if (!providerName || !clientId) {
      throw new Error("Provider identifier and client ID are required.");
    }

    let url;
    try {
      url = new URL(issuer);
    } catch (_) {
      throw new Error("OIDC issuer must be a valid HTTPS URL.");
    }
    if (
      url.protocol !== "https:" ||
      !url.hostname ||
      url.username ||
      url.password ||
      url.search ||
      url.hash
    ) {
      throw new Error(
        "OIDC issuer must be an HTTPS URL with no credentials, query or fragment.",
      );
    }
    if (!existing && !page.querySelector("#OidSecret").value) {
      throw new Error("A client secret is required for a new provider.");
    }
    const override = page.querySelector("#PortOverride").value.trim();
    if (
      override &&
      (!/^[0-9]+$/.test(override) ||
        Number(override) < 1 ||
        Number(override) > 65535)
    ) {
      throw new Error("Port override must be a number between 1 and 65535.");
    }
    if (
      page.dataset.ssoLoadedProvider &&
      page.dataset.ssoLoadedProvider !== providerName
    ) {
      throw new Error(
        "Provider identifier changed. Use New provider or reload the original provider.",
      );
    }
    return { issuer, portOverride: override ? Number(override) : null };
  },
  saveProvider: async (page, providerName) => {
    const saveButton = page.querySelector("#SaveProvider");
    if (saveButton.disabled) return;

    saveButton.disabled = true;
    try {
      const name = providerName.trim();
      const formElements = ssoConfigurationPage.listArgumentsByType(page);
      const config = await ApiClient.getPluginConfiguration(
        ssoConfigurationPage.pluginUniqueId,
      );
      config.OidConfigs ||= {};
      const existing = Object.hasOwn(config.OidConfigs, name)
        ? config.OidConfigs[name]
        : null;

      if (existing && page.dataset.ssoLoadedProvider !== name) {
        throw new Error("Load the existing provider before editing it.");
      }

      const { issuer, portOverride } = ssoConfigurationPage.validateProvider(
        page,
        name,
        existing,
      );
      const next = { ...(existing || {}) };
      formElements.text_fields.forEach((id) => {
        next[id] = page.querySelector("#" + id).value || null;
      });
      formElements.json_fields.forEach((id) => {
        const text = page.querySelector("#" + id).value.trim();
        next[id] = text ? JSON.parse(text) : null;
      });
      formElements.check_fields.forEach((id) => {
        next[id] = page.querySelector("#" + id).checked;
      });
      formElements.text_list_fields.forEach((id) => {
        next[id] = ssoConfigurationPage.parseTextList(
          page.querySelector("#" + id),
        );
      });
      formElements.folder_list_fields.forEach((id) => {
        next[id] = ssoConfigurationPage.serializeEnabledFolders(
          page.querySelector("#" + id),
        );
      });
      formElements.role_map_fields.forEach((id) => {
        next[id] = ssoConfigurationPage.serializeRoleMappings(
          page.querySelector("#" + id),
        );
      });

      // Leave the identity dictionaries in the fetched provider object intact.
      next.OidEndpoint = issuer;
      next.PortOverride = portOverride;
      if (!page.querySelector("#OidSecret").value && existing) {
        next.OidSecret = existing.OidSecret;
      }
      config.OidConfigs[name] = next;

      const result = await ApiClient.updatePluginConfiguration(
        ssoConfigurationPage.pluginUniqueId,
        config,
      );
      Dashboard.processPluginConfigurationUpdateResult(result);
      await ssoConfigurationPage.loadConfiguration(page);
      await ssoConfigurationPage.loadProvider(page, name);
      page.querySelector("#selectProvider").value = name;
      ssoConfigurationPage.setProviderFeedback(
        page,
        "Provider settings saved. Re-test SSO login.",
      );
    } catch (error) {
      ssoConfigurationPage.setProviderFeedback(
        page,
        "Save failed: " + (error?.message || error),
        true,
      );
    } finally {
      saveButton.disabled = false;
    }
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
  normalizeJellyfinUserId: (value) => {
    const entered = value.trim();
    const matches =
      /^[0-9a-f]{32}$/i.test(entered) ||
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(
        entered,
      );
    if (!matches) {
      return null;
    }
    const hex = entered.replace(/-/g, "").toLowerCase();
    return [
      hex.slice(0, 8),
      hex.slice(8, 12),
      hex.slice(12, 16),
      hex.slice(16, 20),
      hex.slice(20),
    ].join("-");
  },
  getLinkUserId: (page) => {
    const field = page.querySelector("#sso-link-user-id");
    const normalized = ssoConfigurationPage.normalizeJellyfinUserId(
      field.value,
    );
    if (normalized) {
      field.value = normalized;
    }
    return normalized;
  },
  formatLinkError: async (error) => {
    const response = error?.response || error;
    const status = response?.status ?? error?.statusCode;
    let details = typeof error === "string" ? error : "";

    if (!details && typeof response?.text === "function") {
      try {
        details = await response.text();
      } catch (_) {
        // Some API clients consume the response body before rejecting.
      }
    }

    if (!details) {
      details =
        error?.responseText ||
        error?.responseJSON?.message ||
        error?.responseJSON?.title ||
        error?.message ||
        error?.statusText ||
        "";
    }

    if (typeof details !== "string") {
      details = String(details || "");
    }

    if (details.trim().startsWith("{")) {
      try {
        const payload = JSON.parse(details);
        details =
          payload.detail ||
          payload.message ||
          payload.title ||
          payload.error ||
          details;
      } catch (_) {
        // A non-JSON body should still be displayed.
      }
    }

    const readable = String(details).trim() || "Request was rejected.";
    return status ? `HTTP ${status} — ${readable}` : readable;
  },
  linkRequest: async (options) => {
    try {
      const response = await ApiClient.fetch(options);
      if (response?.ok === false) {
        throw response;
      }
      return response;
    } catch (error) {
      throw new Error(await ssoConfigurationPage.formatLinkError(error));
    }
  },
  createIdentityLink: async (page) => {
    const provider = page.querySelector("#sso-link-provider").value;
    const userId = ssoConfigurationPage.getLinkUserId(page);
    const issuer = page.querySelector("#sso-link-issuer").value.trim();
    const subject = page.querySelector("#sso-link-subject").value.trim();
    const username = page.querySelector("#sso-link-username").value.trim();

    if (!provider || !userId || !issuer || !subject || !username) {
      ssoConfigurationPage.setLinkStatus(
        page,
        "Enter a provider, valid Jellyfin user ID, issuer, subject, and username.",
      );
      return;
    }
    if (
      !window.confirm(
        `Link OIDC subject "${subject}" to Jellyfin user "${userId}"?`,
      )
    ) {
      return;
    }

    ssoConfigurationPage.setLinkStatus(page, "Saving identity link...");
    try {
      await ssoConfigurationPage.linkRequest({
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
      ssoConfigurationPage.setLinkStatus(page, "Identity link saved.");
      await ssoConfigurationPage.listIdentityLinks(page);
    } catch (error) {
      ssoConfigurationPage.setLinkStatus(page, `Link failed: ${error.message}`);
    }
  },
  listIdentityLinks: async (page) => {
    const userId = ssoConfigurationPage.getLinkUserId(page);
    const results = page.querySelector("#sso-admin-link-results");
    results.replaceChildren();
    if (!userId) {
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
      const providers = await response.json();
      let linkCount = 0;

      for (const [provider, names] of Object.entries(providers)) {
        for (const name of names) {
          const row = document.createElement("div");
          row.className = "sso-identity-link-row";
          row.innerHTML = `
            <span class="sso-identity-link-label"></span>
            <button type="button" is="emby-button" class="raised button-delete emby-button sso-identity-unlink">Unlink</button>
          `;
          row.querySelector(".sso-identity-link-label").textContent =
            `${provider}: ${name}`;
          const button = row.querySelector(".sso-identity-unlink");
          button.setAttribute("aria-label", `Unlink ${name} from ${provider}`);
          button.addEventListener("click", async () => {
            if (
              !window.confirm(
                `Remove "${name}" from Jellyfin user "${userId}"?`,
              )
            ) {
              return;
            }
            button.disabled = true;
            try {
              await ssoConfigurationPage.linkRequest({
                type: "DELETE",
                url: ApiClient.getUrl(
                  `sso/oid/Link/${encodeURIComponent(provider)}/${userId}/${encodeURIComponent(name)}`,
                ),
              });
              await ssoConfigurationPage.listIdentityLinks(page);
            } catch (error) {
              button.disabled = false;
              ssoConfigurationPage.setLinkStatus(
                page,
                `Unlink failed: ${error.message}`,
              );
            }
          });
          results.appendChild(row);
          linkCount += 1;
        }
      }

      ssoConfigurationPage.setLinkStatus(
        page,
        linkCount
          ? `${linkCount} identity link${linkCount === 1 ? "" : "s"} found.`
          : "No identity links for this Jellyfin user.",
      );
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

export { ssoConfigurationPage };

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

  view.querySelector("#NewProvider").addEventListener("click", () => {
    ssoConfigurationPage.resetProviderForm(view);
    view.querySelector("#OidProviderName").focus();
  });

  view.querySelector("#ToggleSecret").addEventListener("click", () => {
    ssoConfigurationPage.setSecretVisible(
      view,
      view.querySelector("#OidSecret").type === "password",
    );
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

  view.querySelector("#sso-link-user-id").addEventListener("blur", () => {
    ssoConfigurationPage.getLinkUserId(view);
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
