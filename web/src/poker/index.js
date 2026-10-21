export function mount(root, boot) {
  root.replaceChildren(document.createTextNode('poker'));
  return {
    update(params) {},
    unmount() {
      root.replaceChildren();
    },
  };
}
