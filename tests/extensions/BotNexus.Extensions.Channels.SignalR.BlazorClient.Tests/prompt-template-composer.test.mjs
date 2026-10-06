import fs from 'node:fs';
import vm from 'node:vm';
import assert from 'node:assert/strict';
const source = fs.readFileSync(process.argv[2], 'utf8');
const context = { window: {}, document: { activeElement: null }, Event: class { constructor(type, options) { this.type=type; this.bubbles=options?.bubbles; } } };
vm.createContext(context); vm.runInContext(source, context);
const events=[];
const element={
 value:'prefix SELECT suffix\r\n🙂', selectionStart:7, selectionEnd:13,
 dataset:{composerContext:'agent-1:conv-1'},
 focus(){context.document.activeElement=this;}, dispatchEvent(e){events.push(e);},
 setRangeText(text,start,end,mode){ assert.equal(mode,'end'); this.value=this.value.slice(0,start)+text+this.value.slice(end); this.selectionStart=this.selectionEnd=start+text.length; }
};
const result=context.window.chatComposer.replaceSelection(element,'中\r\n🙂','agent-1:conv-1');
assert.equal(result.value,'prefix 中\r\n🙂 suffix\r\n🙂'); assert.equal(result.selectionStart,12); assert.equal(result.selectionEnd,12);
assert.equal(events.length,1); assert.equal(events[0].type,'input');
element.dataset.composerContext='agent-1:conv-2'; const before=element.value;
assert.throws(()=>context.window.chatComposer.replaceSelection(element,'STALE','agent-1:conv-1'),/context changed/i);
assert.equal(element.value,before);
// Node has no browser editing transaction/undo stack. C# pins setRangeText; this executes selection, CRLF, Unicode and the stale guard.
console.log('prompt-template composer execution passed');
