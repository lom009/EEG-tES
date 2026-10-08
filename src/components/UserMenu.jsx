import { useEffect, useRef, useState } from "react";
import { LogOut } from "lucide-react";
import "./UserMenu.css";

export function UserMenu({ triggerClassName = "avatar", onLogout }) {
  const [open, setOpen] = useState(false);
  const menuRef = useRef(null);

  useEffect(() => {
    if (!open) return undefined;

    const handlePointerDown = (event) => {
      if (!menuRef.current?.contains(event.target)) setOpen(false);
    };
    const handleKeyDown = (event) => {
      if (event.key === "Escape") setOpen(false);
    };

    document.addEventListener("pointerdown", handlePointerDown);
    document.addEventListener("keydown", handleKeyDown);
    return () => {
      document.removeEventListener("pointerdown", handlePointerDown);
      document.removeEventListener("keydown", handleKeyDown);
    };
  }, [open]);

  return (
    <div className="user-menu" ref={menuRef}>
      <button
        className={triggerClassName}
        type="button"
        aria-label="当前用户 TA"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((value) => !value)}
      >
        <span>TA</span>
        <i aria-hidden="true" />
      </button>

      {open ? (
        <div className="user-menu__popover" role="menu" aria-label="用户菜单">
          <div className="user-menu__identity">
            <strong>TA</strong>
            <span>当前用户</span>
          </div>
          <button
            className="user-menu__logout"
            type="button"
            role="menuitem"
            onClick={() => {
              setOpen(false);
              onLogout?.();
            }}
          >
            <LogOut size={16} aria-hidden="true" />
            <span>退出登录</span>
          </button>
        </div>
      ) : null}
    </div>
  );
}
