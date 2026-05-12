import { attachEditor, setFiles, getFiles } from './editor'
import { SAMPLE_NAMES, loadSample } from './samples'

let playerIframe: HTMLIFrameElement | null = null
let currentSample = '01_triangle'
let syncTimer: number | null = null

const $sample  = document.querySelector<HTMLSelectElement>('#sample-select')!
const $restart = document.querySelector<HTMLButtonElement>('#restart-btn')!
const $log     = document.getElementById('log')!
const $status  = document.getElementById('status')!

for (const s of SAMPLE_NAMES) {
  const o = document.createElement('option')
  o.value = s; o.textContent = s
  $sample.appendChild(o)
}
$sample.value = currentSample

$sample.addEventListener('change', async () => {
  if (anyDirty()) {
    if (!confirm('未保存の変更があります。破棄してサンプル切替しますか?')) {
      $sample.value = currentSample
      return
    }
  }
  currentSample = $sample.value
  const files = await loadSample(currentSample)
  setFiles(files as any)
  await restart()
})

$restart.addEventListener('click', () => restart())

document.addEventListener('keydown', (e) => {
  if (e.altKey && (e.key === 'r' || e.key === 'R')) {
    e.preventDefault()
    restart()
  }
})

window.addEventListener('message', (e) => {
  const d = e.data || {}
  if (d.type === 'log') {
    addLog(String(d.msg ?? ''), d.level || 'log')
  }
})

attachEditor(document.querySelector<HTMLDivElement>('#editor')!, (_path, _content) => {
  if (syncTimer) clearTimeout(syncTimer)
  syncTimer = window.setTimeout(syncDirtyNow, 300)
})

function anyDirty(): boolean {
  for (const f of getFiles().values()) if (f.dirty) return true
  return false
}

function syncDirtyNow() {
  if (!playerIframe?.contentWindow) return
  const files: Record<string, string> = {}
  for (const [p, f] of getFiles()) if (f.dirty) files[p] = f.content
  if (Object.keys(files).length === 0) return
  playerIframe.contentWindow.postMessage({ type: 'syncFiles', files }, '*')
  $status.textContent = `synced ${Object.keys(files).length} file(s)`
}

function addLog(msg: string, level = 'log') {
  const line = document.createElement('div')
  line.textContent = msg
  if (level !== 'log') line.className = level
  $log.appendChild(line)
  $log.scrollTop = $log.scrollHeight
  while ($log.children.length > 500) $log.removeChild($log.firstChild!)
}

async function restart() {
  $status.textContent = 'restarting...'
  if (playerIframe) playerIframe.remove()
  $log.innerHTML = ''
  playerIframe = document.createElement('iframe')
  playerIframe.src = '/player.html'
  document.getElementById('player-mount')!.appendChild(playerIframe)
  await waitForMsg('playerReady')
  const all: Record<string, string> = {}
  for (const [p, f] of getFiles()) all[p] = f.content
  playerIframe.contentWindow!.postMessage(
    { type: 'setFiles', files: all, entry: currentSample }, '*')
  $status.textContent = `running ${currentSample}`
}

function waitForMsg(type: string): Promise<MessageEvent> {
  return new Promise((resolve) => {
    const h = (e: MessageEvent) => {
      if ((e.data || {}).type === type) {
        window.removeEventListener('message', h)
        resolve(e)
      }
    }
    window.addEventListener('message', h)
  })
}

// boot
loadSample(currentSample).then((files) => {
  setFiles(files as any)
  restart()
}).catch((e) => {
  addLog('failed to load initial sample: ' + e.message, 'err')
})
