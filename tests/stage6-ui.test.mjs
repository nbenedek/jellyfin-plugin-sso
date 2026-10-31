import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const js = await readFile(path.join(root, "SSO-Auth/Config/config.js"), "utf8");
const moduleUrl = "data:text/javascript;base64," + Buffer.from(js).toString("base64");
const { ssoConfigurationPage: sso } = await import(moduleUrl);

function formPage(overrides = {}) {
  const inputs = {
    OidProviderName: { id: "OidProviderName", value: "keycloak" },
    OidEndpoint: {
      id: "OidEndpoint",
      value: "https://idp.example/realms/apps",
    },
    OidClientId: { id: "OidClientId", value: "jellyfin" },
    OidSecret: { id: "OidSecret", value: "" },
    PortOverride: { id: "PortOverride", value: "" },
    Enabled: { id: "Enabled", checked: true },
  };
  for (const [name, value] of Object.entries(overrides)) {
    inputs[name] = { ...inputs[name], ...value };
  }

  const form = {
    querySelectorAll(selector) {
      const ids = {
        ".sso-text": [
          "OidProviderName",
          "OidEndpoint",
          "OidClientId",
          "OidSecret",
          "PortOverride",
        ],
        ".sso-json": [],
        ".sso-line-list": [],
        ".sso-toggle": ["Enabled"],
      };
      return (ids[selector] || []).map((id) => inputs[id]);
    },
  };
  const feedback = {
    textContent: "",
    classList: { toggle() {} },
  };
  const save = { disabled: false };
  const select = { value: "" };
  const folders = {
    querySelectorAll() {
      return [{ checked: true, getAttribute: () => "movies-library-guid" }];
    },
  };
  const mappings = { querySelectorAll: () => [], replaceChildren() {} };
  const secretButton = { setAttribute() {}, textContent: "" };
  const elements = {
    "#sso-new-oidc-provider": form,
    "#sso-provider-feedback": feedback,
    "#SaveProvider": save,
    "#selectProvider": select,
    "#EnabledFolders": folders,
    "#FolderRoleMapping": mappings,
    "#ToggleSecret": secretButton,
  };
  Object.entries(inputs).forEach(([id, input]) => {
    elements["#" + id] = input;
  });
  const page = {
    dataset: { ssoLoadedProvider: "keycloak" },
    querySelector(selector) {
      if (!(selector in elements)) {
        throw new Error("Unexpected selector " + selector);
      }
      return elements[selector];
    },
  };
  return { page, feedback, inputs };
}

test("valid provider requires HTTPS authority without query or credentials", () => {
  const { page, inputs } = formPage();
  assert.equal(
    sso.validateProvider(page, "keycloak", { OidSecret: "stored" }).issuer,
    "https://idp.example/realms/apps",
  );

  for (const issuer of [
    "http://idp.example/realms/apps",
    "https://user:pass@idp.example/realms/apps",
    "https://idp.example/realms/apps?token=test",
    "https://idp.example/realms/apps#fragment",
    "not-a-url",
  ]) {
    inputs.OidEndpoint.value = issuer;
    assert.throws(
      () => sso.validateProvider(page, "keycloak", { OidSecret: "stored" }),
      /HTTPS/,
      issuer,
    );
  }
});

test("provider switch without explicit load is rejected", () => {
  const { page } = formPage();
  assert.throws(
    () => sso.validateProvider(page, "other", { OidSecret: "stored" }),
    /Provider identifier changed/,
  );
});

test("new provider requires a client secret and validates the override port", () => {
  const { page, inputs } = formPage();
  page.dataset.ssoLoadedProvider = "";
  assert.throws(
    () => sso.validateProvider(page, "brand-new", null),
    /client secret/,
  );
  inputs.OidSecret.value = "new-secret";
  inputs.PortOverride.value = "70000";
  assert.throws(
    () => sso.validateProvider(page, "brand-new", null),
    /between 1 and 65535/,
  );
  inputs.PortOverride.value = "443";
  assert.equal(sso.validateProvider(page, "brand-new", null).portOverride, 443);
});

test("saving a loaded provider preserves immutable subject links and secret", async () => {
  const { page, feedback, inputs } = formPage();
  inputs.Enabled.checked = false;

  const existing = {
    OidEndpoint: "https://idp.example/realms/apps",
    OidClientId: "jellyfin",
    OidSecret: "stored-secret",
    Enabled: true,
    CanonicalLinks: { legacy: "some-user" },
    SubjectLinks: { "oidc-v1-test": "user-guid" },
    SubjectNames: { "oidc-v1-test": "alice" },
  };
  let updated;
  globalThis.ApiClient = {
    getPluginConfiguration: async () => ({
      OidConfigs: { keycloak: structuredClone(existing) },
    }),
    updatePluginConfiguration: async (_id, config) => {
      updated = config;
      return {};
    },
  };
  globalThis.Dashboard = {
    processPluginConfigurationUpdateResult() {},
  };

  // The tested save path performs a refresh after a successful write.
  const oldConfig = sso.loadConfiguration;
  const oldProvider = sso.loadProvider;
  sso.loadConfiguration = async () => {};
  sso.loadProvider = async () => {};
  try {
    await sso.saveProvider(page, "keycloak");
  } finally {
    sso.loadConfiguration = oldConfig;
    sso.loadProvider = oldProvider;
  }

  assert.ok(updated, feedback.textContent);
  const next = updated.OidConfigs.keycloak;
  assert.deepEqual(next.SubjectLinks, existing.SubjectLinks);
  assert.deepEqual(next.SubjectNames, existing.SubjectNames);
  assert.deepEqual(next.CanonicalLinks, existing.CanonicalLinks);
  assert.equal(next.OidSecret, "stored-secret");
  assert.equal(next.Enabled, false);
  assert.deepEqual(next.EnabledFolders, ["movies-library-guid"]);
  assert.equal(page.querySelector("#SaveProvider").disabled, false);
});

test("loading another provider resets unchecked and blank fields", async () => {
  const { page, inputs, feedback } = formPage();
  globalThis.ApiClient = {
    getPluginConfiguration: async () => ({
      OidConfigs: {
        keycloak: {
          OidEndpoint: "https://idp.example/realms/apps",
          OidClientId: "first",
          OidSecret: "secret-one",
          Enabled: true,
        },
        other: {
          OidEndpoint: "https://second.example/realms/apps",
          OidClientId: "second",
          OidSecret: "",
          Enabled: false,
        },
      },
    }),
  };
  const oldFolders = sso.populateFolders;
  const oldMappings = sso.populateRoleMappings;
  sso.populateFolders = async () => {};
  sso.populateRoleMappings = async () => {};
  try {
    await sso.loadProvider(page, "keycloak");
    assert.equal(inputs.Enabled.checked, true);
    assert.equal(inputs.OidSecret.value, "secret-one");
    assert.equal(page.dataset.ssoLoadedProvider, "keycloak");

    await sso.loadProvider(page, "other");
    assert.equal(inputs.Enabled.checked, false);
    assert.equal(inputs.OidSecret.value, "");
    assert.equal(inputs.OidClientId.value, "second");
    assert.equal(page.dataset.ssoLoadedProvider, "other");
    assert.match(feedback.textContent, /Loaded provider/);
  } finally {
    sso.populateFolders = oldFolders;
    sso.populateRoleMappings = oldMappings;
  }
});

test("admin page is grouped and the credential input is masked", async () => {
  const html = await readFile(
    path.join(root, "SSO-Auth/Config/configPage.html"),
    "utf8",
  );
  assert.match(html, /id="NewProvider"/);
  assert.match(html, /id="OidSecret"\s+type="password"/);
  assert.match(html, /class="sso-advanced-options"/);
  assert.match(html, /class="sso-identity-management"/);
  assert.match(html, /id="sso-provider-feedback"/);

  const plugin = await readFile(
    path.join(root, "SSO-Auth/SSOPlugin.cs"),
    "utf8",
  );
  assert.match(plugin, /DisplayName = "SSO Authentication"/);
  assert.match(plugin, /EnableInMainMenu = true/);
});
