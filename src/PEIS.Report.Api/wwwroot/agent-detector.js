/**
 * PEIS 打印助手客户端自动检测与闭环安装组件
 * 
 * 职责：
 * 1. 自动探针检测当前工作站是否已连接 PrintAgent
 * 2. 离线时提供一键下载 Setup.exe 安装包（服务端已预设 ServerUrl，无需用户解压和配置）
 * 3. 下载后实时轮询检测，安装启动后自动转为就绪状态
 */
(function(window) {
  'use strict';

  const PeisAgentDetector = {
    /**
     * 探测当前工作站的打印助手状态
     * @param {Object} [params]
     * @param {string} [params.stationId] 工作站编号
     * @param {string} [params.clientIp] 客户端IP（可选）
     * @returns {Promise<Object>}
     */
    async probe(params = {}) {
      const query = new URLSearchParams();
      if (params.stationId) query.set('stationId', params.stationId);
      if (params.clientIp) query.set('clientIp', params.clientIp);
      
      const url = '/api/agent/probe' + (query.toString() ? '?' + query.toString() : '');
      const resp = await fetch(url, { method: 'GET', headers: { 'Accept': 'application/json' } });
      if (!resp.ok) {
        throw new Error('探针检测服务异常 (HTTP ' + resp.status + ')');
      }
      return await resp.json();
    },

    /**
     * 获取定制安装包下载地址
     * @param {Object} [params]
     * @param {string} [params.stationId]
     * @param {string} [params.serverUrl]
     * @returns {string}
     */
    getDownloadUrl(params = {}) {
      const query = new URLSearchParams();
      if (params.stationId) query.set('stationId', params.stationId);
      if (params.serverUrl) query.set('serverUrl', params.serverUrl);
      return '/api/agent/download-setup' + (query.toString() ? '?' + query.toString() : '');
    },

    /**
     * 在指定容器挂载检测与状态提示横幅
     * @param {HTMLElement|string} container 容器元素或选择器
     * @param {Object} options 配置项
     */
    mountBanner(container, options = {}) {
      const el = typeof container === 'string' ? document.querySelector(container) : container;
      if (!el) return null;

      const getStationId = typeof options.stationId === 'function' ? options.stationId : () => options.stationId || '';
      let timer = null;
      let isDownloading = false;
      let lastStatus = null;

      const render = (state) => {
        lastStatus = state;
        if (state.loading) {
          el.innerHTML = `
            <div style="background:#f8f9fa;border:1px solid #dee2e6;border-radius:8px;padding:12px 16px;margin-bottom:16px;display:flex;align-items:center;gap:12px;font-size:14px;color:#495057;">
              <span style="display:inline-block;animation:spin 1s linear infinite;">⏳</span>
              <span>正在检测本机打印助手连接状态...</span>
            </div>
          `;
          return;
        }

        if (state.error) {
          el.innerHTML = `
            <div style="background:#fff3cd;border:1px solid #ffeeba;border-radius:8px;padding:12px 16px;margin-bottom:16px;font-size:14px;color:#856404;">
              <strong>⚠️ 打印助手检测异常：</strong> ${state.error}
              <button type="button" id="peis-btn-retry" style="margin-left:12px;padding:3px 8px;font-size:12px;border:1px solid #ffeeba;background:#fff;border-radius:4px;cursor:pointer;">重试</button>
            </div>
          `;
          el.querySelector('#peis-btn-retry')?.addEventListener('click', () => refresh());
          return;
        }

        if (!state.online) {
          const downloadUrl = PeisAgentDetector.getDownloadUrl({ stationId: getStationId() });
          el.innerHTML = `
            <div style="background:#fff8e6;border:1px solid #ffd591;border-radius:8px;padding:14px 18px;margin-bottom:16px;font-size:14px;color:#ad4e00;box-shadow:0 2px 4px rgba(0,0,0,0.03);">
              <div style="display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:12px;">
                <div style="display:flex;align-items:center;gap:10px;">
                  <span style="font-size:20px;">⚠️</span>
                  <div>
                    <div style="font-weight:600;font-size:15px;color:#d46b08;">未检测到本机打印助手 (IP: ${state.clientIp || '未知'})</div>
                    <div style="color:#8c4300;margin-top:2px;font-size:13px;">
                      ${isDownloading ? '已发起下载，请直接运行安装包（全自动配置并启动，无需解压）。正在等待客户端上线...' : '未启动打印助手将无法执行静默打印与硬件直连。服务器已预设配置，点击即可一键安装。'}
                    </div>
                  </div>
                </div>
                <div>
                  <a id="peis-btn-download" href="${downloadUrl}" download style="display:inline-flex;align-items:center;gap:6px;background:#1890ff;color:#fff;text-decoration:none;padding:8px 16px;border-radius:6px;font-weight:500;font-size:13px;box-shadow:0 2px 0 rgba(0,0,0,0.045);transition:background 0.2s;">
                    <span>📥</span>
                    <span>一键下载并自动安装</span>
                  </a>
                </div>
              </div>
            </div>
          `;
          el.querySelector('#peis-btn-download')?.addEventListener('click', () => {
            isDownloading = true;
            render({ ...state });
            // 开始快速轮询
            startPolling(2000);
          });
          return;
        }

        // 在线状态
        const printerCount = state.printers ? state.printers.length : 0;
        el.innerHTML = `
          <div style="background:#f6ffed;border:1px solid #b7eb8f;border-radius:8px;padding:12px 18px;margin-bottom:16px;font-size:14px;color:#237804;box-shadow:0 2px 4px rgba(0,0,0,0.02);">
            <div style="display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:10px;">
              <div style="display:flex;align-items:center;gap:10px;">
                <span style="font-size:18px;">✅</span>
                <div>
                  <span style="font-weight:600;">打印助手已就绪</span>
                  <span style="color:#52c41a;margin-left:8px;font-size:13px;">● 在线</span>
                  <div style="font-size:12px;color:#595959;margin-top:2px;">
                    工作站: <strong>${state.stationId || '未指定'}</strong> | 计算机: ${state.machineName || state.clientIp || '-'} | 可用打印机: ${printerCount} 台 | 版本: v${state.version || '1.0'}
                  </div>
                </div>
              </div>
              <div>
                <button type="button" id="peis-btn-refresh" style="background:#fff;border:1px solid #d9d9d9;padding:4px 10px;border-radius:4px;font-size:12px;color:#595959;cursor:pointer;">刷新状态</button>
              </div>
            </div>
          </div>
        `;
        el.querySelector('#peis-btn-refresh')?.addEventListener('click', () => refresh());
      };

      const refresh = async () => {
        try {
          const res = await PeisAgentDetector.probe({ stationId: getStationId() });
          render(res);
          if (options.onStatusChange) options.onStatusChange(res);
          if (res.online && options.onOnline) options.onOnline(res);
          if (res.online && isDownloading) {
            isDownloading = false;
            startPolling(options.interval || 10000);
          }
        } catch (err) {
          render({ error: err.message });
          if (options.onStatusChange) options.onStatusChange({ online: false, error: err.message });
        }
      };

      const startPolling = (intervalMs) => {
        if (timer) clearInterval(timer);
        timer = setInterval(refresh, intervalMs);
      };

      render({ loading: true });
      refresh();
      startPolling(options.interval || 4000);

      return {
        refresh,
        destroy() {
          if (timer) clearInterval(timer);
        },
        getStatus() {
          return lastStatus;
        }
      };
    }
  };

  window.PeisAgentDetector = PeisAgentDetector;
})(window);
