// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
import vscode, { ExtensionContext } from "vscode";
import fse from "fs-extra";
import path from "path";
import crypto from "crypto";
import { LanguageClient } from "vscode-languageclient/node";
import {
  createDeploymentDataMessage,
  createGetAccessTokenResultMessage,
  createGetDeploymentScopeResultMessage,
  createGetStateResultMessage,
  createPickParamsFileResultMessage,
  ViewMessage,
} from "./messages";
import { getDeploymentDataRequestType } from "../../language";
import { Disposable } from "../../utils/disposable";
import { debounce } from "../../utils/time";
import { getLogger } from "../../utils/logger";
import { TreeManager } from "../../tree/TreeManager";
import {
  callWithTelemetryAndErrorHandlingSync,
  IActionContext,
} from "@microsoft/vscode-azext-utils";
import { GlobalStateKeys } from "../../globalState";
import { DeployPaneState } from "./models";

export class DeployPaneView extends Disposable {
  public static viewType = "bicep.deployPane";

  private readonly onDidDisposeEmitter: vscode.EventEmitter<void>;
  private readonly onDidChangeViewStateEmitter: vscode.EventEmitter<vscode.WebviewPanelOnDidChangeViewStateEvent>;

  private readyToRender = false;

  private constructor(
    private readonly extensionContext: ExtensionContext,
    private readonly context: IActionContext,
    private readonly treeManager: TreeManager,
    private readonly languageClient: LanguageClient,
    private readonly webviewPanel: vscode.WebviewPanel,
    private readonly extensionUri: vscode.Uri,
    private readonly documentUri: vscode.Uri,
  ) {
    super();

    this.onDidDisposeEmitter = new vscode.EventEmitter<void>();
    this.onDidChangeViewStateEmitter = this.register(
      new vscode.EventEmitter<vscode.WebviewPanelOnDidChangeViewStateEvent>(),
    );

    this.register(
      this.webviewPanel.webview.onDidReceiveMessage(
        // eslint-disable-next-line jest/unbound-method
        this.handleDidReceiveMessage,
        this,
      ),
    );

    if (!this.isDisposed) {
      this.webviewPanel.webview.html = this.createWebviewHtml();
    }

    this.registerMultiple(
      // eslint-disable-next-line jest/unbound-method
      this.webviewPanel.onDidDispose(this.dispose, this),
      this.webviewPanel.onDidChangeViewState((e) =>
        this.onDidChangeViewStateEmitter.fire(e),
      ),
    );
  }

  public get onDidDispose(): vscode.Event<void> {
    return this.onDidDisposeEmitter.event;
  }

  public get onDidChangeViewState(): vscode.Event<vscode.WebviewPanelOnDidChangeViewStateEvent> {
    return this.onDidChangeViewStateEmitter.event;
  }

  public static create(
    extensionContext: ExtensionContext,
    context: IActionContext,
    treeManager: TreeManager,
    languageClient: LanguageClient,
    viewColumn: vscode.ViewColumn,
    extensionUri: vscode.Uri,
    documentUri: vscode.Uri,
  ): DeployPaneView {
    const visualizerTitle = `Deploy ${path.basename(documentUri.fsPath)}`;
    const webviewPanel = vscode.window.createWebviewPanel(
      DeployPaneView.viewType,
      visualizerTitle,
      viewColumn,
      {
        enableScripts: true,
        retainContextWhenHidden: true,
      },
    );

    return new DeployPaneView(
      extensionContext,
      context,
      treeManager,
      languageClient,
      webviewPanel,
      extensionUri,
      documentUri,
    );
  }

  public static revive(
    extensionContext: ExtensionContext,
    context: IActionContext,
    treeManager: TreeManager,
    languageClient: LanguageClient,
    webviewPanel: vscode.WebviewPanel,
    extensionUri: vscode.Uri,
    documentUri: vscode.Uri,
  ): DeployPaneView {
    return new DeployPaneView(
      extensionContext,
      context,
      treeManager,
      languageClient,
      webviewPanel,
      extensionUri,
      documentUri,
    );
  }

  public reveal(): void {
    this.webviewPanel.reveal();
  }

  public dispose(): void {
    super.dispose();

    this.webviewPanel.dispose();

    // Final cleanup.
    this.onDidDisposeEmitter.fire();
    this.onDidDisposeEmitter.dispose();
  }

  // Do "fire and forget" since there's no need to wait on rendering.
  public render = debounce(() => this.doRender());

  private async doRender() {
    if (this.isDisposed || !this.readyToRender) {
      return;
    }

    let document: vscode.TextDocument;
    try {
      document = await vscode.workspace.openTextDocument(this.documentUri);
    } catch {
      this.webviewPanel.webview.html = this.createDocumentNotFoundHtml();
      return;
    }

    if (this.isDisposed) {
      return;
    }

    const deploymentData = await this.languageClient.sendRequest(
      getDeploymentDataRequestType,
      {
        textDocument:
          this.languageClient.code2ProtocolConverter.asTextDocumentIdentifier(
            document,
          ),
      },
    );

    if (this.isDisposed) {
      return;
    }

    try {
      await this.webviewPanel.webview.postMessage(
        createDeploymentDataMessage(
          this.documentUri.fsPath,
          deploymentData.templateJson,
          deploymentData.parametersJson,
          deploymentData.errorMessage,
        ),
      );
    } catch (error) {
      // Race condition: the webview was closed before receiving the message,
      // which causes "Unknown webview handle" error.
      getLogger().debug((error as Error).message ?? error);
    }
  }

  private async handleDidReceiveMessage(message: ViewMessage) {
    switch (message.kind) {
      case "READY": {
        this.readyToRender = true;
        this.render();
        return;
      }
      case "GET_STATE": {
        const deployPaneState: Record<string, DeployPaneState> =
          this.extensionContext.globalState.get(
            GlobalStateKeys.deployPaneStateKey,
          ) || {};
        const filteredState = deployPaneState[this.documentUri.toString()];

        await this.webviewPanel.webview.postMessage(
          createGetStateResultMessage(filteredState),
        );
        return;
      }
      case "SAVE_STATE": {
        const deployPaneState: Record<string, DeployPaneState> =
          this.extensionContext.globalState.get(
            GlobalStateKeys.deployPaneStateKey,
          ) || {};
        deployPaneState[this.documentUri.toString()] = message.state;

        await this.extensionContext.globalState.update(
          GlobalStateKeys.deployPaneStateKey,
          deployPaneState,
        );
        return;
      }
      case "PICK_PARAMS_FILE": {
        const parametersFileUri = await this.context.ui.showOpenDialog({
          canSelectMany: false,
          openLabel: "Select Parameters file",
          filters: { "Parameters files": ["json"] },
        });
        const parameterFile = await fse.readFile(
          parametersFileUri[0].fsPath,
          "utf-8",
        );
        await this.webviewPanel.webview.postMessage(
          createPickParamsFileResultMessage(
            parametersFileUri[0].fsPath,
            parameterFile,
          ),
        );
        return;
      }
      case "GET_ACCESS_TOKEN": {
        try {
          switch (message.scope.scopeType) {
            case "resourceGroup": {
              const scopeId = `/subscriptions/${message.scope.subscriptionId}/resourceGroups/${message.scope.resourceGroup}`;
              const treeItem =
                await this.treeManager.azResourceGroupTreeItem.findTreeItem(
                  scopeId,
                  this.context,
                );
              if (!treeItem) {
                throw `Failed to find authenticated context for scope ${scopeId}`;
              }

              const accessToken =
                await treeItem.subscription.credentials.getToken();
              await this.webviewPanel.webview.postMessage(
                createGetAccessTokenResultMessage(accessToken),
              );
              return;
            }
            case "subscription": {
              const scopeId = `/subscriptions/${message.scope.subscriptionId}`;
              const treeItem =
                await this.treeManager.azLocationTree.findTreeItem(
                  scopeId,
                  this.context,
                );
              if (!treeItem) {
                throw `Failed to find authenticated context for scope ${scopeId}`;
              }

              const accessToken =
                await treeItem.subscription.credentials.getToken();
              await this.webviewPanel.webview.postMessage(
                createGetAccessTokenResultMessage(accessToken),
              );
              return;
            }
          }
        } catch (error) {
          await this.webviewPanel.webview.postMessage(
            createGetAccessTokenResultMessage(undefined, error),
          );
        }

        return;
      }
      case "GET_DEPLOYMENT_SCOPE": {
        switch (message.scopeType) {
          case "resourceGroup": {
            const treeItem =
              await this.treeManager.azResourceGroupTreeItem.showTreeItemPicker(
                "",
                this.context,
              );
            await this.webviewPanel.webview.postMessage(
              createGetDeploymentScopeResultMessage({
                scopeType: "resourceGroup",
                subscriptionId: treeItem.subscription.subscriptionId,
                resourceGroup: treeItem.label,
              }),
            );
            return;
          }
          case "subscription": {
            const treeItem =
              await this.treeManager.azLocationTree.showTreeItemPicker(
                "",
                this.context,
              );
            await this.webviewPanel.webview.postMessage(
              createGetDeploymentScopeResultMessage({
                scopeType: "subscription",
                subscriptionId: treeItem.subscription.subscriptionId,
                location: treeItem.label,
              }),
            );
            return;
          }
        }

        return;
      }
      case "PUBLISH_TELEMETRY": {
        callWithTelemetryAndErrorHandlingSync(
          message.eventName,
          (telemetryActionContext) => {
            telemetryActionContext.errorHandling.suppressDisplay = true;
            telemetryActionContext.telemetry.properties = message.properties;
          },
        );
        return;
      }
    }
  }

  private createWebviewHtml() {
    const { cspSource } = this.webviewPanel.webview;
    const nonce = crypto.randomBytes(16).toString("hex");
    const scriptUri = this.webviewPanel.webview.asWebviewUri(
      vscode.Uri.joinPath(this.extensionUri, "out", "deployPane.js"),
    );

    return `
      <!DOCTYPE html>
      <html lang="en">
      <head>
        <meta charset="UTF-8">
        <!--
        Use a content security policy to only allow loading images from our extension directory,
        and only allow scripts that have a specific nonce.
        -->
        <meta http-equiv="Content-Security-Policy" content="default-src 'self' https://management.azure.com; style-src ${cspSource} 'unsafe-inline'; img-src ${cspSource} data:; script-src 'nonce-${nonce}' vscode-webview-resource:;">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
      </head>
      <body>
        <div id="root"></div>
        <script nonce="${nonce}" src="${scriptUri}" />
      </body>
      </html>`;
  }

  private createDocumentNotFoundHtml() {
    const documentName = path.basename(this.documentUri.fsPath);

    return `
      <!DOCTYPE html>
      <html lang="en">
      <head>
        <meta charset="UTF-8">
        <meta name="viewport" content="width=device-width, initial-scale=1.0">
      </head>
      <body>
        <div class="vscode-body">${documentName} not found. It might be deleted or renamed.</div>
      </body>
      </html>`;
  }
}
