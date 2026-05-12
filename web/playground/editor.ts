import { EditorView, basicSetup } from 'codemirror'
import { StreamLanguage } from '@codemirror/language'
import { lua } from '@codemirror/legacy-modes/mode/lua'
import { c as clike } from '@codemirror/legacy-modes/mode/clike'
import { oneDark } from '@codemirror/theme-one-dark'

export type EditorFile = { content: string; dirty: boolean; initial: string }

let view: EditorView | null = null
let files = new Map<string, EditorFile>()
let activePath: string | null = null
let onChangeCb: ((path: string, content: string) => void) | null = null

// TODO(Phase 7): wire per-tab language via a CodeMirror Compartment.
// langFor is the picker for .slang vs .lua highlight modes.
function langFor(path: string) {
  if (path.endsWith('.slang')) return StreamLanguage.define(clike)
  return StreamLanguage.define(lua)
}

function rebuildTabs() {
  const tabs = document.getElementById('tabs')!
  tabs.innerHTML = ''
  for (const [path, f] of files) {
    const el = document.createElement('div')
    el.className = 'tab' + (path === activePath ? ' active' : '') + (f.dirty ? ' dirty' : '')
    el.textContent = path
    el.addEventListener('click', () => selectTab(path))
    tabs.appendChild(el)
  }
}

export function attachEditor(container: HTMLElement,
                              onChange: (path: string, content: string) => void) {
  onChangeCb = onChange
  view = new EditorView({
    doc: '',
    extensions: [
      basicSetup,
      oneDark,
      EditorView.theme({
        '&': { height: '100%' },
        '.cm-scroller': { overflow: 'auto' },
      }),
      EditorView.updateListener.of((u) => {
        if (!u.docChanged || !activePath || !onChangeCb) return
        const content = u.state.doc.toString()
        const f = files.get(activePath)
        if (!f) return
        f.content = content
        const wasDirty = f.dirty
        f.dirty = content !== f.initial
        if (wasDirty !== f.dirty) rebuildTabs()
        onChangeCb(activePath, content)
      }),
    ],
    parent: container,
  })
}

export function setFiles(newFiles: Map<string, EditorFile>) {
  files = newFiles
  const first = files.keys().next().value as string | undefined
  activePath = first ?? null
  rebuildTabs()
  if (view && activePath) {
    const f = files.get(activePath)!
    view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: f.content } })
  }
}

export function getFiles(): Map<string, EditorFile> { return files }

export function selectTab(path: string) {
  const f = files.get(path)
  if (!f || !view) return
  activePath = path
  view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: f.content } })
  rebuildTabs()
}
