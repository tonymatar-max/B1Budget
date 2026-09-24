import { createContext, useContext } from 'react'
import type { Me } from './api'

export interface Auth {
  me: Me
  isAdmin: boolean
  /** Admins and managers approve submissions. */
  isApprover: boolean
  refresh: () => void
  logout: () => void
}

export const AuthContext = createContext<Auth | null>(null)

export function useAuth(): Auth {
  const a = useContext(AuthContext)
  if (!a) throw new Error('useAuth outside the signed-in app')
  return a
}
